using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>存储与日志面板增强的后端验收：分布统计 / 事件持久化 / 保留天数 / 预检增强。</summary>
public sealed class StorageDisplayEnhancementTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    private (StoragePathManager Manager, string Root) CreateManager()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "HaloStorageDisplay",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(temp);
        _tempRoots.Add(temp);
        var bootstrap = Path.Combine(temp, "bootstrap.json");
        var root = Path.Combine(temp, "DataRoot");
        var fallback = Path.Combine(temp, "Fallback");
        var manager = StoragePathManager.Initialize(bootstrap, root, fallback);
        return (manager, manager.CurrentDataRoot);
    }

    private static HostBridgeFacade CreateFacade(StoragePathManager manager) => new(null, manager);

    // ===== 存储分布统计 =====

    [Fact]
    public void GetStorageBreakdown_空目录保留零值项且总和一致()
    {
        var (manager, root) = CreateManager();
        var breakdown = manager.GetStorageBreakdown();

        Assert.Equal(root, breakdown.RootPath);
        Assert.Contains(
            breakdown.Items,
            item => item.Name == "logs" && item.SizeBytes == 0 && item.FileCount == 0
        );
        Assert.Equal(breakdown.Items.Sum(item => item.SizeBytes), breakdown.TotalBytes);
    }

    [Fact]
    public void GetStorageBreakdown_写入文件后按目录统计且总和一致()
    {
        var (manager, root) = CreateManager();
        var logsDir = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logsDir, "app.log"), new string('x', 100));
        File.WriteAllText(Path.Combine(logsDir, "llm.log"), new string('y', 200));

        var breakdown = manager.GetStorageBreakdown();
        var logs = Assert.Single(breakdown.Items.Where(item => item.Name == "logs"));
        Assert.Equal(300, logs.SizeBytes);
        Assert.Equal(2, logs.FileCount);
        Assert.Equal(breakdown.Items.Sum(item => item.SizeBytes), breakdown.TotalBytes);
    }

    [Fact]
    public void SafeSumDirectory_不存在目录返回零不抛异常()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "HaloStorageDisplay",
            Guid.NewGuid().ToString("N"),
            "nope"
        );
        var (bytes, count) = StoragePathManager.SafeSumDirectory(missing);
        Assert.Equal(0, bytes);
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetStorageBreakdown_包含backups且计入总量()
    {
        var (manager, root) = CreateManager();
        var backupDir = Path.Combine(root, "backups", "ws", "sess");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "a.bak"), new string('b', 128));
        File.WriteAllText(Path.Combine(backupDir, "a.bak.meta.json"), "{}");
        File.WriteAllText(Path.Combine(backupDir, "index.json"), "[]");

        var breakdown = manager.GetStorageBreakdown();
        var backups = Assert.Single(breakdown.Items.Where(item => item.Name == "backups"));
        Assert.Equal(128 + 2 + 2, backups.SizeBytes);
        Assert.Equal(3, backups.FileCount);
        Assert.Equal(breakdown.Items.Sum(item => item.SizeBytes), breakdown.TotalBytes);
    }

    [Fact]
    public async Task ChangeDataRootAsync_迁移backups保留目录结构()
    {
        var (manager, root) = CreateManager();
        var backupDir = Path.Combine(root, "backups", "ws", "sess");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "a.bak"), new string('b', 128));
        File.WriteAllText(Path.Combine(backupDir, "a.bak.meta.json"), "{}");
        File.WriteAllText(Path.Combine(backupDir, "index.json"), "[]");

        var newRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloStorageDisplay",
            Guid.NewGuid().ToString("N"),
            "MigratedRoot"
        );
        _tempRoots.Add(Path.GetDirectoryName(newRoot)!);

        var result = await manager.ChangeDataRootAsync(newRoot, migrateExistingData: true);

        Assert.True(result.Success, result.Message);
        Assert.True(result.MigratedFileCount >= 3);
        var migrated = Path.Combine(newRoot, "backups", "ws", "sess");
        Assert.True(File.Exists(Path.Combine(migrated, "a.bak")));
        Assert.True(File.Exists(Path.Combine(migrated, "a.bak.meta.json")));
        Assert.True(File.Exists(Path.Combine(migrated, "index.json")));
    }

    // ===== 事件持久化 =====

    [Fact]
    public void StorageEventStore_追加后落盘为可解析JSON()
    {
        var (manager, _) = CreateManager();
        StorageEventStore.Append(
            manager,
            "storage:changed",
            new { oldDataRootPath = "C:\\old", newDataRootPath = "D:\\new" },
            1000
        );

        var path = StorageEventStore.GetEventsFilePath(manager);
        Assert.True(File.Exists(path));
        Assert.EndsWith("storage-events.jsonl", path);
        var lines = File.ReadAllLines(path);
        Assert.Single(lines);
        using var document = JsonDocument.Parse(lines[0]);
        Assert.Equal("storage:changed", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(1000, document.RootElement.GetProperty("timestamp").GetInt64());
    }

    [Fact]
    public void StorageEventStore_读取尾部N条倒序返回_文件不存在返回空()
    {
        var (manager, _) = CreateManager();
        Assert.Empty(StorageEventStore.ReadRecent(manager, 20));

        StorageEventStore.Append(manager, "storage:changed", new { n = 1 }, 1000);
        StorageEventStore.Append(manager, "storage:migration-progress", new { n = 2 }, 2000);
        StorageEventStore.Append(manager, "storage:changed", new { n = 3 }, 3000);

        var recent = StorageEventStore.ReadRecent(manager, 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(3000, recent[0].Timestamp);
        Assert.Equal(2000, recent[1].Timestamp);
    }

    [Fact]
    public void StorageEventStore_写入失败时静默不抛()
    {
        var (manager, _) = CreateManager();
        var path = StorageEventStore.GetEventsFilePath(manager);
        File.WriteAllText(path, "existing");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var exception = Record.Exception(
                () => StorageEventStore.Append(manager, "storage:changed", new { }, 1)
            );
            Assert.Null(exception);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ===== 保留天数独立设置 =====

    [Fact]
    public async Task SetLogRetentionDays_只写设置不删除任何日志()
    {
        var (manager, root) = CreateManager();
        var oldLog = Path.Combine(root, "logs", "app-old.log");
        File.WriteAllText(oldLog, "keep me");

        var facade = CreateFacade(manager);
        var response = await facade.SetLogRetentionDays(7);

        Assert.True(response.Ok);
        Assert.Equal(7, response.Data);
        Assert.Equal(7, manager.LoadAppSettings().LogRetentionDays);
        Assert.True(File.Exists(oldLog));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(400, 365)]
    public async Task SetLogRetentionDays_越界值被钳制(int input, int expected)
    {
        var (manager, _) = CreateManager();
        var response = await CreateFacade(manager).SetLogRetentionDays(input);
        Assert.Equal(expected, response.Data);
        Assert.Equal(expected, manager.LoadAppSettings().LogRetentionDays);
    }

    // ===== 预检字段增强 =====

    [Fact]
    public void ValidateTargetRootPath_目标非空时标记TargetNonEmpty()
    {
        var (manager, _) = CreateManager();
        var target = Path.Combine(
            Path.GetTempPath(),
            "HaloStorageDisplay",
            Guid.NewGuid().ToString("N"),
            "NonEmpty"
        );
        Directory.CreateDirectory(target);
        _tempRoots.Add(Path.GetDirectoryName(target)!);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "data");

        var result = manager.ValidateTargetRootPath(target);

        Assert.True(result.IsValid);
        Assert.True(result.TargetNonEmpty);
        Assert.True(result.RequiredSpaceMb >= 0);
        Assert.Null(result.RiskCode);
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
