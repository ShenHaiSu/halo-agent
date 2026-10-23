using System.Text.Json;
using HaloAgent.Bridge.Contracts;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.BridgeContract.Tests;

/// <summary>
/// need21-B05 Step2: 桥 DTO JSON 快照（小驼峰；字段增删必须显式更新快照，PR 可见）。
/// canonical 口径为 Contracts.BridgeResponse；Core.Models 旧信封必须逐字节一致（迁移完成前双写不断言失败即红）。
/// </summary>
public sealed class BridgeResponseSnapshotTests
{
    private static string SnapshotDir()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(dir, "__snapshots__");
            if (Directory.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent))
                break;
            dir = parent;
        }

        throw new DirectoryNotFoundException("找不到 __snapshots__ 目录（Content 未复制到输出？）。");
    }

    private static string ReadSnapshot(string name) =>
        File.ReadAllText(Path.Combine(SnapshotDir(), name)).Trim();

    [Fact]
    public void SuccessEnvelope_MatchesSnapshot()
    {
        var env = BridgeResponse<string>.Success("ok", "done");
        string json = JsonSerializer.Serialize(env, HaloJsonOptions.CompactCamelCase);
        Assert.Equal(ReadSnapshot("bridge_response_success.json"), json);
    }

    [Fact]
    public void FailEnvelope_MatchesSnapshot()
    {
        var env = BridgeResponse<string>.Fail("PATH_OUTSIDE_WORKSPACE", "越界");
        string json = JsonSerializer.Serialize(env, HaloJsonOptions.CompactCamelCase);
        Assert.Equal(ReadSnapshot("bridge_response_fail.json"), json);
    }

    [Fact]
    public void LegacyCoreEnvelope_SerializesIdentically()
    {
        var canonical = JsonSerializer.Serialize(
            BridgeResponse<string>.Success("ok", "done"),
            HaloJsonOptions.CompactCamelCase
        );
        var legacy = JsonSerializer.Serialize(
            Host.Desktop.Core.Models.BridgeResponse<string>.Success("ok", "done"),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Equal(canonical, legacy);
    }
}
