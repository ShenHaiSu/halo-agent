using System.Collections.Concurrent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区历史库维护原语：备份快照、原地压实、备份清理共用同一工作区级维护门。
/// 统计读取不经过此门；压实永不自动创建新备份；忙时直接拒绝，不做同步等待。
/// </summary>
public sealed class WorkspaceDatabaseMaintenance
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MaintenanceLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly StoragePathManager _storage;

    public WorkspaceDatabaseMaintenance(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public static SemaphoreSlim GetMaintenanceLock(string key) =>
        MaintenanceLocks.GetOrAdd(key ?? string.Empty, _ => new SemaphoreSlim(1, 1));

    internal static string NormalizeKey(string workspaceId) =>
        (workspaceId ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>工作区级备份：checkpoint 后 VACUUM INTO 临时文件再原子替换旧备份。</summary>
    public async Task<WorkspaceBackupInfo> BackupAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
            throw new ArgumentException("Workspace ID cannot be empty.", nameof(workspaceId));

        string dbPath = _storage.GetWorkspaceDbPath(wsId);
        string backupPath = _storage.GetWorkspaceBackupPath(wsId);
        var gate = GetMaintenanceLock("maintenance:" + NormalizeKey(wsId));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("STORAGE_MAINTENANCE_BUSY");

        bool createdDatabase = false;
        bool walExisted = File.Exists(dbPath + "-wal");
        bool shmExisted = File.Exists(dbPath + "-shm");
        string tmpPath = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!File.Exists(dbPath))
            {
                new SessionHistoryDb(_storage).EnsureWorkspaceDatabase(wsId);
                createdDatabase = true;
            }

            string? dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            TryDeleteStaleBackups(backupPath);
            CheckpointBestEffort(dbPath);

            using (
                var conn = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false,
                    }.ToString()
                )
            )
            {
                conn.Open();
                using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $tmp;";
                vacuum.Parameters.AddWithValue("$tmp", tmpPath);
                vacuum.ExecuteNonQuery();
            }

            File.Move(tmpPath, backupPath, overwrite: true);
            if (!walExisted)
                TryDeleteFile(dbPath + "-wal");
            if (!shmExisted)
                TryDeleteFile(dbPath + "-shm");
            TryDeleteStaleBackups(backupPath);

            return new WorkspaceBackupInfo
            {
                WorkspaceId = wsId,
                BackupPath = backupPath,
                CreatedAtUtc = DateTime.UtcNow,
            };
        }
        catch
        {
            TryDeleteFile(tmpPath);
            if (createdDatabase)
            {
                TryDeleteFile(dbPath);
                TryDeleteFile(dbPath + "-wal");
                TryDeleteFile(dbPath + "-shm");
            }

            if (!walExisted)
                TryDeleteFile(dbPath + "-wal");
            if (!shmExisted)
                TryDeleteFile(dbPath + "-shm");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 手动原地压实：checkpoint → VACUUM → checkpoint，测量前后文件大小。
    /// 忙 / checkpoint 失败 / 空间不足一律执行前失败，不破坏原库与旧备份。
    /// </summary>
    public async Task<WorkspaceCompactionResult> CompactAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
        {
            return new WorkspaceCompactionResult
            {
                Success = false,
                ErrorCode = "INVALID_WORKSPACE_ID"
            };
        }

        string dbPath;
        try
        {
            dbPath = _storage.GetWorkspaceDbPath(wsId);
        }
        catch
        {
            return new WorkspaceCompactionResult
            {
                Success = false,
                ErrorCode = "INVALID_WORKSPACE_ID"
            };
        }

        if (!File.Exists(dbPath))
        {
            return new WorkspaceCompactionResult
            {
                Success = false,
                ErrorCode = "DATABASE_NOT_FOUND"
            };
        }

        var gate = GetMaintenanceLock("maintenance:" + NormalizeKey(wsId));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new WorkspaceCompactionResult
            {
                Success = false,
                ErrorCode = "STORAGE_MAINTENANCE_BUSY"
            };
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            long beforeBytes = SafeLength(dbPath);

            if (!CheckpointTruncate(dbPath))
            {
                return FailCompaction(beforeBytes, "CHECKPOINT_BUSY");
            }

            if (!HasEnoughFreeSpace(dbPath))
            {
                return FailCompaction(beforeBytes, "INSUFFICIENT_DISK_SPACE", checkpointed: true);
            }

            try
            {
                using var conn = SessionHistoryDb.OpenConnection(dbPath);
                using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                string code = IsBusyError(ex) ? "DATABASE_BUSY" : "COMPACTION_FAILED";
                LogMaintenance(
                    $"workspaceId={wsId} stage=Vacuum result={code} exception={ex.GetType().Name}"
                );
                return FailCompaction(beforeBytes, code, checkpointed: true);
            }
            catch (Exception ex)
            {
                LogMaintenance(
                    $"workspaceId={wsId} stage=Vacuum result=COMPACTION_FAILED exception={ex.GetType().Name}"
                );
                return FailCompaction(beforeBytes, "COMPACTION_FAILED", checkpointed: true);
            }

            bool checkpointed = CheckpointTruncate(dbPath);
            long afterBytes = SafeLength(dbPath);
            return new WorkspaceCompactionResult
            {
                Success = true,
                BeforeBytes = beforeBytes,
                AfterBytes = afterBytes,
                WalCheckpointed = checkpointed,
                Warning = checkpointed ? null : "CHECKPOINT_PARTIAL",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMaintenance(
                $"workspaceId={wsId} stage=Compact result=COMPACTION_FAILED exception={ex.GetType().Name}"
            );
            return new WorkspaceCompactionResult
            {
                Success = false,
                ErrorCode = "COMPACTION_FAILED",
                BeforeBytes = SafeLength(dbPath),
                AfterBytes = SafeLength(dbPath),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>手动备份清理：只删 history.db.bak 与同名前缀 *.tmp，主库与 WAL/SHM 永不触碰。</summary>
    public async Task<WorkspaceBackupCleanupResult> DeleteBackupAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
        {
            return new WorkspaceBackupCleanupResult
            {
                Success = false,
                ErrorCode = "INVALID_WORKSPACE_ID"
            };
        }

        string backupPath;
        try
        {
            backupPath = _storage.GetWorkspaceBackupPath(wsId);
        }
        catch
        {
            return new WorkspaceBackupCleanupResult
            {
                Success = false,
                ErrorCode = "INVALID_WORKSPACE_ID"
            };
        }

        var gate = GetMaintenanceLock("maintenance:" + NormalizeKey(wsId));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new WorkspaceBackupCleanupResult
            {
                Success = false,
                ErrorCode = "STORAGE_MAINTENANCE_BUSY"
            };
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            long freed = 0;
            bool existed = File.Exists(backupPath);
            if (existed)
                freed += SafeLength(backupPath);

            foreach (string stale in ListStaleBackups(backupPath))
                freed += SafeLength(stale);

            try
            {
                if (existed)
                    File.Delete(backupPath);
                foreach (string stale in ListStaleBackups(backupPath))
                    TryDeleteFile(stale);
            }
            catch (Exception ex)
            {
                LogMaintenance(
                    $"workspaceId={wsId} stage=DeleteBackup result=DELETE_FAILED exception={ex.GetType().Name}"
                );
                return new WorkspaceBackupCleanupResult
                {
                    Success = false,
                    ErrorCode = "BACKUP_DELETE_FAILED",
                    FreedBytes = 0,
                    BackupExisted = true,
                };
            }

            return new WorkspaceBackupCleanupResult
            {
                Success = true,
                FreedBytes = Math.Max(0, freed),
                BackupExisted = existed,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool CheckpointTruncate(string dbPath)
    {
        try
        {
            using var conn = SessionHistoryDb.OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;

            object busy = reader.GetValue(0);
            object logFrames = reader.FieldCount > 1 ? reader.GetValue(1) : 0;
            object checkpointed = reader.FieldCount > 2 ? reader.GetValue(2) : 0;
            long busyValue = ToInt64(busy);
            long logValue = ToInt64(logFrames);
            long doneValue = ToInt64(checkpointed);
            try
            {
                TimelineObservability.ReportCheckpoint(busyValue == 0);
            }
            catch { }

            return busyValue == 0 && logValue == doneValue;
        }
        catch
        {
            try
            {
                TimelineObservability.ReportCheckpoint(false);
            }
            catch { }

            return false;
        }
    }

    private static void CheckpointBestEffort(string dbPath)
    {
        try
        {
            CheckpointTruncate(dbPath);
        }
        catch { }
    }

    private static WorkspaceCompactionResult FailCompaction(
        long beforeBytes,
        string code,
        bool checkpointed = false
    ) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            BeforeBytes = beforeBytes,
            AfterBytes = beforeBytes,
            WalCheckpointed = checkpointed,
        };

    private static bool HasEnoughFreeSpace(string dbPath)
    {
        try
        {
            long fileBytes = SafeLength(dbPath);
            long required = Math.Max(fileBytes + (16L * 1024L * 1024L), 32L * 1024L * 1024L);
            string? root = Path.GetPathRoot(Path.GetFullPath(dbPath));
            if (string.IsNullOrEmpty(root))
                return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;
            return drive.AvailableFreeSpace >= required;
        }
        catch
        {
            // 磁盘探测失败不阻断压实，由 SQLite 自身在空间不足时报错。
            return true;
        }
    }

    private static bool IsBusyError(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    private static long ToInt64(object? value)
    {
        if (value == null || value == DBNull.Value)
            return 0;
        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> ListStaleBackups(string backupPath)
    {
        var result = new List<string>();
        try
        {
            string? dir = Path.GetDirectoryName(backupPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return result;
            string fileName = Path.GetFileName(backupPath);
            foreach (string file in Directory.GetFiles(dir, fileName + ".*.tmp"))
                result.Add(file);

            // 兼容历史残留命名：history.db.bak 后直接追加的各类 tmp（如 .bak.abc.tmp）。
            foreach (string file in Directory.GetFiles(dir, fileName + "*"))
            {
                if (string.Equals(file, backupPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (
                    file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains(file)
                )
                    result.Add(file);
            }
        }
        catch { }

        return result;
    }

    internal static void TryDeleteStaleBackups(string backupPath)
    {
        foreach (string stale in ListStaleBackups(backupPath))
            TryDeleteFile(stale);
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败由调用方异常或残留告警负责，不伪造成功。
        }
    }

    private static void LogMaintenance(string message)
    {
        try
        {
            HaloLogger.App.Warn("[WorkspaceMaintenance] " + message);
        }
        catch
        {
            // 日志失败不得改变维护阶段结果。
        }
    }
}
