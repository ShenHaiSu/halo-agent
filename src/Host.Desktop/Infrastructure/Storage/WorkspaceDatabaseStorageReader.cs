using Host.Desktop.Core.Models;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区历史库页级存储明细读取器：只读 PRAGMA 快照，不执行 checkpoint / VACUUM / integrity_check。
/// 单库失败降级为未测量，不阻断其他工作区统计。
/// </summary>
public sealed class WorkspaceDatabaseStorageReader
{
    private readonly StoragePathManager _storage;

    public WorkspaceDatabaseStorageReader(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public IReadOnlyList<WorkspaceDatabaseStorageDetail> MeasureAll()
    {
        var result = new List<WorkspaceDatabaseStorageDetail>();
        string sessionsRoot;
        try
        {
            sessionsRoot = _storage.SessionsDirectory;
        }
        catch
        {
            return result;
        }

        string[] workspaceDirs;
        try
        {
            if (!Directory.Exists(sessionsRoot))
                return result;
            workspaceDirs = Directory.GetDirectories(sessionsRoot);
        }
        catch (UnauthorizedAccessException)
        {
            return result;
        }
        catch (IOException)
        {
            return result;
        }

        foreach (string dir in workspaceDirs)
        {
            string workspaceId;
            try
            {
                workspaceId = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
                continue;

            try
            {
                result.Add(MeasureWorkspace(workspaceId));
            }
            catch
            {
                // 单库测量永不抛向聚合层。
                result.Add(Unmeasured(workspaceId, "READ_FAILED"));
            }
        }

        result.Sort(
            (a, b) =>
                string.Compare(a.WorkspaceId, b.WorkspaceId, StringComparison.OrdinalIgnoreCase)
        );
        return result;
    }

    public WorkspaceDatabaseStorageDetail MeasureWorkspace(string workspaceId)
    {
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
            return Unmeasured(wsId, "INVALID_WORKSPACE_ID");

        string dbPath;
        try
        {
            dbPath = _storage.GetWorkspaceDbPath(wsId);
        }
        catch
        {
            return Unmeasured(wsId, "INVALID_WORKSPACE_ID");
        }

        string backupPath = dbPath + ".bak";
        string walPath = dbPath + "-wal";
        string shmPath = dbPath + "-shm";
        long backupBytes = SafeLength(backupPath);
        long walBytes = SafeLength(walPath);
        long shmBytes = SafeLength(shmPath);
        long tempBytes = SafeTempBytes(dbPath, backupPath);

        if (!File.Exists(dbPath))
        {
            return new WorkspaceDatabaseStorageDetail
            {
                WorkspaceId = wsId,
                DatabasePath = dbPath,
                PhysicalBytes = 0,
                PageSizeBytes = 0,
                PageCount = 0,
                FreePageCount = 0,
                UsedPageBytes = 0,
                ReclaimablePageBytes = 0,
                BackupBytes = backupBytes,
                WalBytes = walBytes,
                ShmBytes = shmBytes,
                TempBytes = tempBytes,
                Measured = false,
                Warning = "NOT_FOUND",
                MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
            };
        }

        long physicalBytes = SafeLength(dbPath);
        try
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                }.ToString()
            );
            conn.Open();
            long pageSize = ReadPragma(conn, "PRAGMA page_size;");
            long pageCount = ReadPragma(conn, "PRAGMA page_count;");
            long freePages = ReadPragma(conn, "PRAGMA freelist_count;");

            if (pageSize <= 0 || pageCount < 0 || freePages < 0 || pageCount < freePages)
            {
                return new WorkspaceDatabaseStorageDetail
                {
                    WorkspaceId = wsId,
                    DatabasePath = dbPath,
                    PhysicalBytes = physicalBytes,
                    PageSizeBytes = Math.Max(0, pageSize),
                    PageCount = Math.Max(0, pageCount),
                    FreePageCount = Math.Max(0, freePages),
                    UsedPageBytes = 0,
                    ReclaimablePageBytes = 0,
                    BackupBytes = backupBytes,
                    WalBytes = walBytes,
                    ShmBytes = shmBytes,
                    TempBytes = tempBytes,
                    Measured = false,
                    Warning = "INVALID_PAGE_STATS",
                    MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
                };
            }

            return new WorkspaceDatabaseStorageDetail
            {
                WorkspaceId = wsId,
                DatabasePath = dbPath,
                PhysicalBytes = physicalBytes,
                PageSizeBytes = pageSize,
                PageCount = pageCount,
                FreePageCount = freePages,
                UsedPageBytes = (pageCount - freePages) * pageSize,
                ReclaimablePageBytes = freePages * pageSize,
                BackupBytes = backupBytes,
                WalBytes = walBytes,
                ShmBytes = shmBytes,
                TempBytes = tempBytes,
                Measured = true,
                Warning = null,
                MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
            };
        }
        catch
        {
            return new WorkspaceDatabaseStorageDetail
            {
                WorkspaceId = wsId,
                DatabasePath = dbPath,
                PhysicalBytes = physicalBytes,
                PageSizeBytes = 0,
                PageCount = 0,
                FreePageCount = 0,
                UsedPageBytes = 0,
                ReclaimablePageBytes = 0,
                BackupBytes = backupBytes,
                WalBytes = walBytes,
                ShmBytes = shmBytes,
                TempBytes = tempBytes,
                Measured = false,
                Warning = "READ_FAILED",
                MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
            };
        }
    }

    private static long ReadPragma(SqliteConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        object? value = cmd.ExecuteScalar();
        if (value == null || value == DBNull.Value)
            return -1;
        return Convert.ToInt64(value);
    }

    private static long SafeLength(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0;
            return new FileInfo(path).Length;
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

    private static long SafeTempBytes(string dbPath, string backupPath)
    {
        long total = 0;
        try
        {
            string? dir = Path.GetDirectoryName(backupPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return 0;
            string prefix = Path.GetFileName(backupPath) + ".";
            foreach (
                string file in Directory.EnumerateFiles(
                    dir,
                    Path.GetFileName(backupPath) + ".*.tmp"
                )
            )
            {
                if (
                    !file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(file, backupPath, StringComparison.OrdinalIgnoreCase)
                )
                {
                    // 仅统计备份残留 tmp；主库侧 journal 交由 SQLite 管理，不在此统计。
                }

                total += SafeLength(file);
            }

            // 主库同目录的 -journal 残留也计入 temp 口径，避免误判为泄漏。
            string journal = dbPath + "-journal";
            total += SafeLength(journal);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return total;
    }

    private WorkspaceDatabaseStorageDetail Unmeasured(string workspaceId, string warning)
    {
        string dbPath;
        try
        {
            dbPath = _storage.GetWorkspaceDbPath(workspaceId ?? string.Empty);
        }
        catch
        {
            dbPath = string.Empty;
        }

        return new WorkspaceDatabaseStorageDetail
        {
            WorkspaceId = workspaceId ?? string.Empty,
            DatabasePath = dbPath,
            PhysicalBytes = 0,
            PageSizeBytes = 0,
            PageCount = 0,
            FreePageCount = 0,
            UsedPageBytes = 0,
            ReclaimablePageBytes = 0,
            BackupBytes = 0,
            WalBytes = 0,
            ShmBytes = 0,
            TempBytes = 0,
            Measured = false,
            Warning = warning,
            MeasuredAtUtc = DateTime.UtcNow.ToString("o"),
        };
    }
}
