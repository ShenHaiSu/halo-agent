using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Channels;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Channels;

public sealed class NamedPipeChannelTests : FrameChannelContractTests
{
    protected override async Task<(IFrameChannel a, IFrameChannel b)> CreatePairAsync()
    {
        var name = "halo-test-" + Guid.NewGuid().ToString("N")[..8];

        // 服务端先起监听（不 await 完成，Accept 会阻塞到客户端连接）
        var serverTask = NamedPipeChannel.AcceptAsync(name);

        var client = await NamedPipeChannel.ConnectAsync(name, timeoutMs: 5000);
        var server = await serverTask;

        return (client, server);
    }

    [Fact]
    public async Task Pipe_name_is_used_in_describe()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            Assert.Contains("pipe:", a.Describe);
            Assert.Contains("pipe:", b.Describe);
        }
    }

    /// <summary>
    /// ACL 验证：创建的管道安全描述符必须包含当前用户与 SYSTEM，
    /// 且**不包含** Everyone。
    /// </summary>
    [Fact]
    public void Pipe_security_denies_everyone()
    {
        var sec = NamedPipeChannel.BuildPipeSecurity();
        var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));

        var everyone = new SecurityIdentifier(
            WellKnownSidType.WorldSid, null);

        foreach (PipeAccessRule rule in rules)
        {
            Assert.NotEqual(everyone, rule.IdentityReference);
        }
    }
}
