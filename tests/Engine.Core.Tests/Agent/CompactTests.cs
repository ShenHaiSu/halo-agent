using HaloAgent.Engine.Core.Tests.TestSupport;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>S18/S19：上下文压缩（保尾 4 条逐字 + 失败不变）。</summary>
public sealed class CompactTests
{
    [Fact]
    public async Task S18_compact_keeps_last_four_verbatim()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 5; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        var tail = f.MemoryMessages().TakeLast(4).Select(m => m.Content).ToArray();
        Assert.Equal(4, tail.Length);

        f.Llm.EnqueueContent("这是摘要");
        var (removed, summary) = await f.Session.CompactAsync(CancellationToken.None);

        Assert.True(removed > 0, "压缩应删掉旧消息");
        Assert.Equal("这是摘要", summary);

        var after = f.MemoryMessages().TakeLast(4).Select(m => m.Content).ToArray();
        Assert.Equal(tail, after);

        f.AssertInvariants(f.MemoryMessages());
    }

    [Fact]
    public async Task S19_compact_failure_leaves_history_untouched()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 3; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        var before = f.MemoryMessages().Select(m => m.RoleWire + "|" + m.Content).ToArray();
        f.Llm.EnqueueThrow(new HttpRequestException("boom"));

        await Assert.ThrowsAnyAsync<Exception>(() => f.Session.CompactAsync(CancellationToken.None));

        var after = f.MemoryMessages().Select(m => m.RoleWire + "|" + m.Content).ToArray();
        Assert.Equal(before, after);

        f.AssertInvariants(f.MemoryMessages());
    }
}
