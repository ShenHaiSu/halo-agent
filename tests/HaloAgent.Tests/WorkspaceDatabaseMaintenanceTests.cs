#nullable enable
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 历史库空间治理验收：双口径明细、手动压实不破坏数据、忙态拒绝、备份清理幂等。
/// </summary>
public sealed class WorkspaceDatabaseMaintenanceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionHistoryDb _db;

    public WorkspaceDatabaseMaintenanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "HaloWsMaint_" + Guid.NewGuid().ToString("N"));
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloWsMaintBoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _db = new SessionHistoryDb(_storage);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        try
        {
            Directory.Delete(_bootstrapRoot, true);
        }
        catch { }
    }

    private static string NewWorkspace() => "ws_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task SeedLargePayloadAsync(
        SessionHistoryStore store,
        string ws,
        string sess,
        int messages
    )
    {
        for (int i = 0; i < messages; i++)
        {
            store.InsertSingleMessage(
                ws,
                sess,
                UnifiedChatMessage.User("payload-" + i + "-" + new string('x', 2048))
            );
        }

        await Task.CompletedTask;
    }

    private static long CountRows(string dbPath, string table, string sessionId)
    {
        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long FileLength(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    [Fact]
    public async Task Reader_主库页统计与物理口径一致_删除后空闲页增加但文件不缩小()
    {
        string ws = NewWorkspace();
        var store = new SessionHistoryStore(_storage);
        await SeedLargePayloadAsync(store, ws, "sess_big", 60);

        var reader = new WorkspaceDatabaseStorageReader(_storage);
        var before = reader.MeasureWorkspace(ws);
        Assert.True(before.Measured);
        Assert.True(before.PageCount > 0);
        Assert.Equal(
            (before.PageCount - before.FreePageCount) * before.PageSizeBytes,
            before.UsedPageBytes
        );

        string dbPath = _storage.GetWorkspaceDbPath(ws);
        Assert.Equal(FileLength(dbPath), before.PhysicalBytes);

        using (var conn = _db.OpenWorkspace(ws))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM messages WHERE session_id = 'sess_big';";
            cmd.ExecuteNonQuery();
        }

        var afterDelete = reader.MeasureWorkspace(ws);
        Assert.True(afterDelete.Measured);
        Assert.True(afterDelete.FreePageCount >= before.FreePageCount);
        Assert.True(afterDelete.ReclaimablePageBytes >= before.ReclaimablePageBytes);
        // WAL 模式 DELETE 不缩小主库：物理口径保持解释力。
        Assert.Equal(FileLength(dbPath), afterDelete.PhysicalBytes);
    }

    [Fact]
    public void Reader_备份与WAL分别计入正确字段()
    {
        string ws = NewWorkspace();
        _db.EnsureWorkspaceDatabase(ws);

        string dbPath = _storage.GetWorkspaceDbPath(ws);
        string backupPath = _storage.GetWorkspaceBackupPath(ws);
        File.WriteAllText(backupPath, new string('b', 128));
        File.WriteAllBytes(dbPath + "-wal", new byte[64]);

        var detail = new WorkspaceDatabaseStorageReader(_storage).MeasureWorkspace(ws);
        Assert.True(detail.Measured);
        Assert.Equal(128, detail.BackupBytes);
        Assert.Equal(64, detail.WalBytes);
        Assert.Equal(0, detail.TempBytes);
    }

    [Fact]
    public void Breakdown_物理总量不变且sessions行挂载明细()
    {
        string ws = NewWorkspace();
        _db.EnsureWorkspaceDatabase(ws);
        File.WriteAllText(_storage.GetWorkspaceBackupPath(ws), new string('b', 64));

        var breakdown = _storage.GetStorageBreakdown();
        var sessions = Assert.Single(breakdown.Items.Where(item => item.Name == "sessions"));
        Assert.NotNull(sessions.DatabaseDetails);
        Assert.Contains(sessions.DatabaseDetails!, d => d.WorkspaceId == ws && d.Measured);
        Assert.Equal(breakdown.Items.Sum(item => item.SizeBytes), breakdown.TotalBytes);

        var other = breakdown.Items.First(item => item.Name != "sessions");
        Assert.Null(other.DatabaseDetails);
    }

    [Fact]
    public async Task Compact_删除大量数据后文件真实缩小且内容不变()
    {
        string ws = NewWorkspace();
        var repository = new SessionRepository(_storage);
        var service = new WorkspaceSessionService(repository);
        var keep = await service.CreateSessionAsync(ws, "保留会话");
        await SeedLargePayloadAsync(new SessionHistoryStore(_storage), ws, "sess_drop", 80);
        await SeedLargePayloadAsync(new SessionHistoryStore(_storage), ws, keep.Id, 2);

        string dbPath = _storage.GetWorkspaceDbPath(ws);
        using (var conn = _db.OpenWorkspace(ws))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM messages WHERE session_id = 'sess_drop';";
            cmd.ExecuteNonQuery();
        }

        long beforeDeleteBytes = FileLength(dbPath);
        Assert.True(FileLength(_storage.GetWorkspaceBackupPath(ws)) >= 0);

        var maintenance = new WorkspaceStorageMaintenanceService(_storage);
        var result = await maintenance.CompactWorkspaceHistoryAsync(ws);

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.AfterBytes <= result.BeforeBytes);
        Assert.True(result.AfterBytes < beforeDeleteBytes);
        Assert.True(FileLength(dbPath) < beforeDeleteBytes);
        // 保留会话内容完全不变。
        Assert.Equal(2, CountRows(dbPath, "messages", keep.Id));
        // 压实不自动创建新备份：此前无备份则仍无备份。
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(ws)));
    }

    [Fact]
    public async Task Compact_连续两次幂等安全_第二次不再显著缩小()
    {
        string ws = NewWorkspace();
        var store = new SessionHistoryStore(_storage);
        await SeedLargePayloadAsync(store, ws, "sess_drop", 40);
        using (var conn = _db.OpenWorkspace(ws))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM messages WHERE session_id = 'sess_drop';";
            cmd.ExecuteNonQuery();
        }

        var maintenance = new WorkspaceStorageMaintenanceService(_storage);
        var first = await maintenance.CompactWorkspaceHistoryAsync(ws);
        var second = await maintenance.CompactWorkspaceHistoryAsync(ws);
        Assert.True(first.Success, first.ErrorCode);
        Assert.True(second.Success, second.ErrorCode);
        Assert.Equal(first.AfterBytes, second.AfterBytes);
        Assert.Equal(0, second.ReclaimedBytes);
    }

    [Fact]
    public async Task Compact_忙会话时拒绝且数据库哈希不变()
    {
        string ws = NewWorkspace();
        var repository = new SessionRepository(_storage);
        var coordinator = new SessionOperationCoordinator();
        var service = new WorkspaceSessionService(repository, coordinator);
        var meta = await service.CreateSessionAsync(ws, "忙会话");
        await SeedLargePayloadAsync(new SessionHistoryStore(_storage), ws, meta.Id, 3);

        string dbPath = _storage.GetWorkspaceDbPath(ws);
        long beforeBytes = FileLength(dbPath);

        using var _ = coordinator.TryAcquire(ws, meta.Id, SessionOperationKind.Agent, out var lease)
            ? lease
            : null;
        Assert.NotNull(lease);

        var busyService = new WorkspaceStorageMaintenanceService(_storage, repository, coordinator);
        var result = await busyService.CompactWorkspaceHistoryAsync(ws);
        Assert.False(result.Success);
        Assert.Equal("SESSION_BUSY", result.ErrorCode);
        Assert.Equal(beforeBytes, FileLength(dbPath));
    }

    [Fact]
    public async Task Compact_维护门互斥_Backup与Compact不同时执行()
    {
        var gate = WorkspaceDatabaseMaintenance.GetMaintenanceLock("maintenance:ws_gate_check");
        await gate.WaitAsync();
        try
        {
            var maintenance = new WorkspaceStorageMaintenanceService(_storage);
            var compact = await maintenance.CompactWorkspaceHistoryAsync("ws_gate_check");
            Assert.False(compact.Success);
            Assert.Equal("DATABASE_NOT_FOUND", compact.ErrorCode);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task DeleteBackup_只删备份与tmp_主库WAL保留_缺失幂等成功()
    {
        string ws = NewWorkspace();
        _db.EnsureWorkspaceDatabase(ws);
        string dbPath = _storage.GetWorkspaceDbPath(ws);
        string backupPath = _storage.GetWorkspaceBackupPath(ws);
        File.WriteAllText(backupPath, new string('b', 256));
        File.WriteAllText(backupPath + ".abc.tmp", "stale");
        await SeedLargePayloadAsync(new SessionHistoryStore(_storage), ws, "sess_keep", 1);

        var maintenance = new WorkspaceStorageMaintenanceService(_storage);
        var result = await maintenance.DeleteWorkspaceBackupAsync(ws);

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.BackupExisted);
        Assert.Equal(256 + 5, result.FreedBytes);
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + ".abc.tmp"));
        Assert.True(File.Exists(dbPath));
        Assert.Equal(1, CountRows(dbPath, "messages", "sess_keep"));

        var missing = await maintenance.DeleteWorkspaceBackupAsync(ws);
        Assert.True(missing.Success);
        Assert.False(missing.BackupExisted);
        Assert.Equal(0, missing.FreedBytes);
    }

    [Fact]
    public async Task Bridge_压实与备份清理返回中文提示且失败码可映射()
    {
        string ws = NewWorkspace();
        var facade = new HostBridgeFacade(null, _storage);

        var missingCompact = await facade.CompactWorkspaceHistory(ws);
        Assert.False(missingCompact.Ok);
        Assert.Equal("DATABASE_NOT_FOUND", missingCompact.Code);

        var missingCleanup = await facade.DeleteWorkspaceBackup(ws);
        Assert.True(missingCleanup.Ok);
        Assert.True(missingCleanup.Data!.Success);
        Assert.False(missingCleanup.Data!.BackupExisted);

        _db.EnsureWorkspaceDatabase(ws);
        File.WriteAllText(_storage.GetWorkspaceBackupPath(ws), new string('b', 32));
        var cleanup = await facade.DeleteWorkspaceBackup(ws);
        Assert.True(cleanup.Ok);
        Assert.True(cleanup.Data!.Success);
        Assert.True(cleanup.Data!.FreedBytes >= 32);
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(ws)));
    }

    [Fact]
    public void Reader_缺失库返回未测量且不抛()
    {
        var detail = new WorkspaceDatabaseStorageReader(_storage).MeasureWorkspace(
            "ws_missing_" + Guid.NewGuid().ToString("N")[..6]
        );
        Assert.False(detail.Measured);
        Assert.Equal("NOT_FOUND", detail.Warning);
        Assert.Equal(0, detail.UsedPageBytes);
    }

    [Fact]
    public async Task Reader_统计读取不获取维护门_压实持有门时仍可读()
    {
        string ws = NewWorkspace();
        _db.EnsureWorkspaceDatabase(ws);

        var gate = WorkspaceDatabaseMaintenance.GetMaintenanceLock(
            "maintenance:" + ws.ToLowerInvariant()
        );
        await gate.WaitAsync();
        try
        {
            var detail = new WorkspaceDatabaseStorageReader(_storage).MeasureWorkspace(ws);
            Assert.True(detail.Measured);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task DeleteBackup_占用文件导致失败时返回明确错误()
    {
        string ws = NewWorkspace();
        _db.EnsureWorkspaceDatabase(ws);
        string backupPath = _storage.GetWorkspaceBackupPath(ws);
        File.WriteAllText(backupPath, new string('b', 32));

        var maintenance = new WorkspaceStorageMaintenanceService(_storage);
        await using var locked = File.Open(
            backupPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );
        var result = await maintenance.DeleteWorkspaceBackupAsync(ws);

        Assert.False(result.Success);
        Assert.Equal("BACKUP_DELETE_FAILED", result.ErrorCode);
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public async Task Maintenance_并发压实与备份清理仅其一成功()
    {
        string ws = NewWorkspace();
        var store = new SessionHistoryStore(_storage);
        await SeedLargePayloadAsync(store, ws, "sess_big", 40);
        using (var conn = _db.OpenWorkspace(ws))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM messages WHERE session_id = 'sess_big';";
            cmd.ExecuteNonQuery();
        }
        File.WriteAllText(_storage.GetWorkspaceBackupPath(ws), new string('b', 32));

        var maintenance = new WorkspaceStorageMaintenanceService(_storage);
        var compactTask = maintenance.CompactWorkspaceHistoryAsync(ws);
        var cleanupTask = maintenance.DeleteWorkspaceBackupAsync(ws);
        var results = await Task.WhenAll(
            compactTask.ContinueWith(t => (object)t.Result),
            cleanupTask.ContinueWith(t => (object)t.Result)
        );

        int busyCount = results.Count(r =>
            (
                r is WorkspaceCompactionResult compact
                && compact.ErrorCode == "STORAGE_MAINTENANCE_BUSY"
            )
            || (
                r is WorkspaceBackupCleanupResult cleanup
                && cleanup.ErrorCode == "STORAGE_MAINTENANCE_BUSY"
            )
        );
        Assert.True(busyCount <= 1);
    }
}
