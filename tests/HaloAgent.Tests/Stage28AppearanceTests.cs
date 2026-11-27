#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 外观配置（config/appearance.json）：dev/release 共用同一后端文件，规避 localStorage 按 origin 隔离；
/// 覆盖「无文件触发迁移 / 桥接读写往返 / 坏 JSON 校验 / 非法值归一 / 坏文件回默认」。
/// </summary>
public sealed class Stage28AppearanceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;

    public Stage28AppearanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage28Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage28Bootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    private const string ValidJson =
        "{\"themeMode\":\"light\",\"themeColor\":\"#f43f5e\",\"fontSize\":14,"
        + "\"chineseFont\":\"Microsoft YaHei UI\",\"englishFont\":\"Segoe UI\","
        + "\"monoFont\":\"Consolas\",\"chatWidthMode\":\"full\",\"sendKeyMode\":\"ctrl-enter\"}";

    [Fact(DisplayName = "S28-01: 无文件时 GetAppearance 返回 APPEARANCE_NOT_FOUND（触发前端迁移，而非当作默认真相源）")]
    public async Task AC01_Missing_Returns_Not_Found()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        var res = await bridge.GetAppearance();
        Assert.False(res.Ok);
        Assert.Equal("APPEARANCE_NOT_FOUND", res.Code);
    }

    [Fact(DisplayName = "S28-02: 桥接读写往返（主题色 + 会话宽度）")]
    public async Task AC02_Bridge_Roundtrip()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        var set = await bridge.SetAppearance(ValidJson);
        Assert.True(set.Ok);
        Assert.True(File.Exists(_storageManager.AppearanceFilePath));

        var get = await bridge.GetAppearance();
        Assert.True(get.Ok);
        Assert.Equal("light", get.Data!.ThemeMode);
        Assert.Equal("#f43f5e", get.Data!.ThemeColor);
        Assert.Equal(14, get.Data!.FontSize);
        Assert.Equal("full", get.Data!.ChatWidthMode);
        Assert.Equal("ctrl-enter", get.Data!.SendKeyMode);
    }

    [Fact(DisplayName = "S28-03: 坏 JSON 返回 APPEARANCE_VALIDATION 且不落盘")]
    public async Task AC03_Bad_Json()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        var res = await bridge.SetAppearance("{ broken [[[");
        Assert.False(res.Ok);
        Assert.Equal("APPEARANCE_VALIDATION", res.Code);
        Assert.False(File.Exists(_storageManager.AppearanceFilePath));
    }

    [Fact(DisplayName = "S28-04: 空 JSON 返回 APPEARANCE_VALIDATION")]
    public async Task AC04_Empty_Json()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        var res = await bridge.SetAppearance("   ");
        Assert.False(res.Ok);
        Assert.Equal("APPEARANCE_VALIDATION", res.Code);
    }

    [Fact(DisplayName = "S28-05: 非法枚举/字号/颜色归一到默认值（与前端 mergeWithDefaults 同语义）")]
    public async Task AC05_Normalize()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        const string json =
            "{\"themeMode\":\"neon\",\"themeColor\":\"rgb(1,2,3)\",\"fontSize\":999,"
            + "\"chatWidthMode\":\"sideways\",\"sendKeyMode\":\"space\"}";
        var set = await bridge.SetAppearance(json);
        Assert.True(set.Ok);

        var get = await bridge.GetAppearance();
        Assert.Equal("dark", get.Data!.ThemeMode);
        Assert.Equal("#3b82f6", get.Data!.ThemeColor);
        Assert.Equal(13, get.Data!.FontSize);
        Assert.Equal("centered", get.Data!.ChatWidthMode);
        Assert.Equal("enter", get.Data!.SendKeyMode);
    }

    [Fact(DisplayName = "S28-06: 坏文件 LoadAppearance 回默认不抛")]
    public void AC06_Corrupt_Returns_Default()
    {
        Directory.CreateDirectory(_storageManager.ConfigDirectory);
        File.WriteAllText(_storageManager.AppearanceFilePath, "{ broken json [[[");
        var s = _storageManager.LoadAppearance();
        Assert.Equal("dark", s.ThemeMode);
        Assert.Equal("#3b82f6", s.ThemeColor);
        Assert.Equal(13, s.FontSize);
        Assert.Equal("centered", s.ChatWidthMode);
    }

    [Fact(DisplayName = "S28-07: 两实例共享同一数据根时 appearance.json 路径一致（dev/release 互通前提）")]
    public void AC07_Path_Follows_DataRoot()
    {
        var other = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        Assert.Equal(_storageManager.AppearanceFilePath, other.AppearanceFilePath);
        Assert.Equal(
            Path.Combine(_tempRoot, "config", "appearance.json"),
            other.AppearanceFilePath
        );
    }
}
