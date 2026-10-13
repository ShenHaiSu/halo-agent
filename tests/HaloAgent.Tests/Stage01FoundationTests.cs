using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HaloAgent.Tests;

public sealed class Stage01FoundationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapFilePath;
    private readonly string _defaultDataRoot;
    private readonly string _fallbackDataRoot;

    public Stage01FoundationTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloAgentStage01Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _bootstrapFilePath = Path.Combine(_tempRoot, "LocalAppData", "HaloAgent", "bootstrap.json");
        _defaultDataRoot = Path.Combine(_tempRoot, "DefaultCDrive", "HaloAgentData");
        _fallbackDataRoot = Path.Combine(_tempRoot, "LocalAppData", "HaloAgent", "Data");
    }

    /// <summary>
    /// 验证 S01-AC02: 首次启动自动创建 %LocalAppData%\HaloAgent\bootstrap.json 引导文件，
    /// 并默认在目标目录（C:\HaloAgentData）下生成 config/、workspaces/、sessions/、logs/、cef-cache/ 子目录及 app-settings.json。
    /// </summary>
    [Fact]
    public void S01_AC02_Initialize_CreatesBootstrapJson_And_DefaultSubDirectories()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);

        Assert.True(File.Exists(_bootstrapFilePath), "bootstrap.json 应当被自动创建");
        Assert.Equal(Path.GetFullPath(_defaultDataRoot), manager.CurrentDataRoot);
        Assert.False(manager.IsUsingFallback);

        Assert.True(Directory.Exists(manager.ConfigDirectory), "config 子目录应当存在");
        Assert.True(Directory.Exists(manager.WorkspacesDirectory), "workspaces 子目录应当存在");
        Assert.True(Directory.Exists(manager.SessionsDirectory), "sessions 子目录应当存在");
        Assert.True(Directory.Exists(manager.LogsDirectory), "logs 子目录应当存在");
        Assert.True(Directory.Exists(manager.CefCacheDirectory), "cef-cache 子目录应当存在");
        Assert.True(File.Exists(manager.AppSettingsFilePath), "app-settings.json 应当被自动创建");
    }

    /// <summary>
    /// 验证 S01-AC03: 修改存储目录时支持“将现有数据与日志迁移至新目录”与“仅切换至新目录（保留原目录不动）”，
    /// 且切换完成后立即更新 bootstrap.json，后续写入均落入新路径。
    /// </summary>
    [Fact]
    public async Task S01_AC03_ChangeDataRoot_SupportsMigrateAndSwitchOnly_AndPersistsBootstrap()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);
        HaloLogger.Initialize(manager);

        // 在原目录写入模拟配置文件和会话文件
        var sampleConfigFile = Path.Combine(manager.ConfigDirectory, "providers.json");
        var sampleSessionFile = Path.Combine(manager.SessionsDirectory, "sess-001.json");
        await File.WriteAllTextAsync(sampleConfigFile, "{\"provider\":\"openai\"}");
        await File.WriteAllTextAsync(sampleSessionFile, "{\"id\":\"sess-001\"}");

        // 1. 测试带迁移切换至新目录 NewRootA
        var newRootA = Path.Combine(_tempRoot, "CustomRootA_HaloAgentData");
        var resultA = await manager.ChangeDataRootAsync(newRootA, migrateExistingData: true);

        Assert.True(resultA.Success);
        Assert.True(resultA.MigratedData);
        Assert.True(resultA.MigratedFileCount >= 2);
        Assert.Equal(Path.GetFullPath(newRootA), manager.CurrentDataRoot);
        Assert.True(File.Exists(Path.Combine(newRootA, "config", "providers.json")));
        Assert.True(File.Exists(Path.Combine(newRootA, "sessions", "sess-001.json")));

        // 验证重启后读取 bootstrap.json 依然指向 NewRootA
        var reloadedManager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);
        Assert.Equal(Path.GetFullPath(newRootA), reloadedManager.CurrentDataRoot);
        Assert.Equal(Path.GetFullPath(_defaultDataRoot), reloadedManager.PreviousDataRoot);

        // 2. 测试仅切换至新目录 NewRootB（不迁移旧文件）
        var newRootB = Path.Combine(_tempRoot, "CustomRootB_Empty");
        var resultB = await reloadedManager.ChangeDataRootAsync(newRootB, migrateExistingData: false);

        Assert.True(resultB.Success);
        Assert.False(resultB.MigratedData);
        Assert.Equal(0, resultB.MigratedFileCount);
        Assert.Equal(Path.GetFullPath(newRootB), reloadedManager.CurrentDataRoot);
        Assert.False(File.Exists(Path.Combine(newRootB, "config", "providers.json")));
        Assert.True(File.Exists(Path.Combine(newRootA, "config", "providers.json")), "原目录旧文件应保留不动");

        // 3. 测试通过 HostBridgeFacade 一键恢复默认路径
        var facade = new HostBridgeFacade(null, reloadedManager, gpuDisabled: false);
        var resetRes = await facade.ResetStoragePathToDefault(migrateData: true);
        Assert.True(resetRes.Ok);
        Assert.NotNull(resetRes.Data);
        Assert.True(resetRes.Data!.Success);
        Assert.Equal(Path.GetFullPath(_defaultDataRoot), reloadedManager.CurrentDataRoot);
    }

    /// <summary>
    /// 验证 S01-AC03 边界保护: 禁止将磁盘根目录或 Windows 系统目录设为存储目录。
    /// </summary>
    [Fact]
    public void S01_AC03_ValidateTargetRootPath_RejectsDriveRootAndSystemDirectories()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);

        var driveRootCheck = manager.ValidateTargetRootPath(@"C:\");
        Assert.False(driveRootCheck.IsValid);
        Assert.Equal("DRIVE_ROOT_FORBIDDEN", driveRootCheck.ReasonCode);

        var windowsDirCheck = manager.ValidateTargetRootPath(@"C:\Windows\System32");
        Assert.False(windowsDirCheck.IsValid);
        Assert.Equal("SYSTEM_PROTECTED_PATH", windowsDirCheck.ReasonCode);
    }

    /// <summary>
    /// 验证 S01-AC04: 三通道日志写入、Bearer / sk-* API Key 自动脱敏（保留前4后4）与过期日志清理。
    /// </summary>
    [Fact]
    public async Task S01_AC04_HaloLogger_WritesThreeChannels_SanitizesSecrets_AndCleansExpiredLogs()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);
        HaloLogger.Initialize(manager);

        HaloLogger.App.Info("Stage 01 Host started.");
        HaloLogger.Llm.Info("Request header Authorization: Bearer sk-abcdef1234567890xyz9876 sent.");
        HaloLogger.Tool.Info("Executed cmd.exe /c echo hello");

        await HaloLogger.FlushAsync(TimeSpan.FromSeconds(5));

        var statuses = manager.GetLogChannelStatuses();
        Assert.Equal(3, statuses.Count);
        Assert.All(statuses, s => Assert.True(s.Exists && s.SizeBytes > 0, $"通道 {s.Channel} 日志文件应存在且非空"));

        var llmChannel = Assert.Single(statuses, s => s.Channel == "llm");
        var llmContent = await File.ReadAllTextAsync(llmChannel.FilePath);
        Assert.DoesNotContain("sk-abcdef1234567890xyz9876", llmContent);
        Assert.Contains("sk-a***9876", llmContent);

        // 制造一个 20 天前的过期日志文件并验证清理
        var expiredLogFile = Path.Combine(manager.LogsDirectory, "app-20200101.log");
        await File.WriteAllTextAsync(expiredLogFile, "old log entry");
        File.SetLastWriteTimeUtc(expiredLogFile, DateTime.UtcNow.AddDays(-20));

        var deletedCount = HaloLogger.CleanupExpiredLogs(retentionDays: 14);
        Assert.Equal(1, deletedCount);
        Assert.False(File.Exists(expiredLogFile));
    }

    /// <summary>
    /// 验证存储路径热切换后，三通道日志器的后续日志自动落入新目录的 logs/ 下。
    /// </summary>
    [Fact]
    public async Task S01_AC03_AC04_LoggerFollowsDynamicStorageRootSwitch()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);
        HaloLogger.Initialize(manager);

        HaloLogger.App.Info("BeforeSwitch: Log in default root.");
        await HaloLogger.FlushAsync(TimeSpan.FromSeconds(3));

        var newRoot = Path.Combine(_tempRoot, "SwitchedLogRoot");
        await manager.ChangeDataRootAsync(newRoot, migrateExistingData: true);

        HaloLogger.App.Info("AfterSwitch: Log in new root after migration.");
        await HaloLogger.FlushAsync(TimeSpan.FromSeconds(3));

        var statuses = manager.GetLogChannelStatuses();
        var appStatus = Assert.Single(statuses, s => s.Channel == "app");
        Assert.StartsWith(Path.GetFullPath(newRoot), Path.GetFullPath(appStatus.FilePath));

        var appLogText = await File.ReadAllTextAsync(appStatus.FilePath);
        Assert.Contains("AfterSwitch: Log in new root after migration.", appLogText);
    }

    /// <summary>
    /// 验证 S01-AC06: HostBridgeFacade 异步 IPC 接口返回格式与状态完整性。
    /// </summary>
    [Fact]
    public async Task S01_AC06_HostBridgeFacade_ReturnsExpectedSystemAndStorageInfo()
    {
        var manager = StoragePathManager.Initialize(_bootstrapFilePath, _defaultDataRoot, _fallbackDataRoot);
        HaloLogger.Initialize(manager);
        var facade = new HostBridgeFacade(null, manager, gpuDisabled: true);

        var sysRes = await facade.GetSystemInfo();
        Assert.True(sysRes.Ok);
        Assert.NotNull(sysRes.Data);
        Assert.Equal(manager.CurrentDataRoot, sysRes.Data!.CurrentDataRoot);
        Assert.True(sysRes.Data.GpuAccelerationDisabled);

        var storageRes = await facade.GetStorageConfig();
        Assert.True(storageRes.Ok);
        Assert.NotNull(storageRes.Data);
        Assert.Equal(3, storageRes.Data!.LogChannels.Count);
    }

    public void Dispose()
    {
        try
        {
            HaloLogger.Flush();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks in temp folder
        }
    }
}
