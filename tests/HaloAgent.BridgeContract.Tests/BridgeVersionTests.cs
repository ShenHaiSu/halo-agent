using System.Text.RegularExpressions;
using HaloAgent.Bridge.Contracts;
using Xunit;

namespace HaloAgent.BridgeContract.Tests;

/// <summary>
/// need21-B05 Step2: 前后端桥版本一致性（后端数值版本 == 前端 version.ts == bridge/types.ts；mismatch 即红）。
/// </summary>
public sealed class BridgeVersionTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "HaloAgent.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException("找不到 HaloAgent.sln（无法定位仓库根）。");
    }

    private static int ReadFrontendVersion(string relativePath)
    {
        string path = Path.Combine(FindRepoRoot(), relativePath);
        string text = File.ReadAllText(path);
        var m = Regex.Match(text, @"BRIDGE_VERSION\s*=\s*(\d+)");
        Assert.True(m.Success, $"前端版本文件未找到 BRIDGE_VERSION：{relativePath}");
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void BackendProtocolVersion_IsExpected()
    {
        Assert.Equal(2, BridgeVersion.ProtocolVersion);
    }

    [Fact]
    public void BackendStringVersion_IsFrozen()
    {
        Assert.Equal("1.0.0", BridgeVersion.Version);
    }

    [Fact]
    public void FrontendVersionTs_MatchesBackend()
    {
        int frontend = ReadFrontendVersion(
            Path.Combine("src", "Web.Client", "src", "shared", "bridge", "protocol", "version.ts")
        );
        Assert.Equal(BridgeVersion.ProtocolVersion, frontend);
    }

    [Fact]
    public void FrontendLegacyTypesTs_RemovedByF05()
    {
        // need21-F05: 旧垫片 src/bridge/* 已物理删除，版本单源仅剩 shared/bridge/protocol/version.ts。
        // 若旧文件仍存在则说明废弃清理未完成，此处显式断言其不存在（取代旧的版本一致断言）。
        string path = Path.Combine(
            FindRepoRoot(),
            Path.Combine("src", "Web.Client", "src", "bridge", "types.ts")
        );
        Assert.False(File.Exists(path), "F05 后旧垫片 src/bridge/types.ts 应已删除");
    }
}
