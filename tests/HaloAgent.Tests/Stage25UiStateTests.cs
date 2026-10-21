#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>UI活跃状态（config/ui-state.json）：切换即写、坏文件回空、桥接读写。</summary>
public sealed class Stage25UiStateTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;

    public Stage25UiStateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage25Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage25Bootstrap_{Guid.NewGuid():N}"
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

    [Fact(DisplayName = "S25-01: 无文件回默认空状态")]
    public void AC01_Missing_Returns_Default()
    {
        var s = _storageManager.LoadUiState();
        Assert.Null(s.LastWorkspaceId);
        Assert.Null(s.LastSessionId);
    }

    [Fact(DisplayName = "S25-02: 读写往返")]
    public async Task AC02_Roundtrip()
    {
        await _storageManager.SaveUiStateAsync(
            new UiActiveState { LastWorkspaceId = "ws_1", LastSessionId = "sess_1" }
        );
        var s = _storageManager.LoadUiState();
        Assert.Equal("ws_1", s.LastWorkspaceId);
        Assert.Equal("sess_1", s.LastSessionId);
        Assert.False(string.IsNullOrWhiteSpace(s.LastActiveAtUtc));
    }

    [Fact(DisplayName = "S25-03: 坏文件回默认不抛")]
    public void AC03_Corrupt_Returns_Default()
    {
        Directory.CreateDirectory(_storageManager.ConfigDirectory);
        File.WriteAllText(_storageManager.UiStateFilePath, "{ broken json [[[");
        var s = _storageManager.LoadUiState();
        Assert.Null(s.LastWorkspaceId);
        Assert.Null(s.LastSessionId);
    }

    [Fact(DisplayName = "S25-04: 空参清零")]
    public async Task AC04_Clear_With_Null()
    {
        await _storageManager.SaveUiStateAsync(
            new UiActiveState { LastWorkspaceId = "ws_1", LastSessionId = "sess_1" }
        );
        var bridge = new HostBridgeFacade(null, _storageManager);
        var setRes = await bridge.SetLastActive(null, null);
        Assert.True(setRes.Ok);
        var getRes = await bridge.GetLastActive();
        Assert.True(getRes.Ok);
        Assert.Null(getRes.Data!.WorkspaceId);
        Assert.Null(getRes.Data!.SessionId);
    }

    [Fact(DisplayName = "S25-05: 桥接读写往返")]
    public async Task AC05_Bridge_Roundtrip()
    {
        var bridge = new HostBridgeFacade(null, _storageManager);
        var setRes = await bridge.SetLastActive("ws_abc", "sess_xyz");
        Assert.True(setRes.Ok);
        var getRes = await bridge.GetLastActive();
        Assert.True(getRes.Ok);
        Assert.Equal("ws_abc", getRes.Data!.WorkspaceId);
        Assert.Equal("sess_xyz", getRes.Data!.SessionId);
    }
}
