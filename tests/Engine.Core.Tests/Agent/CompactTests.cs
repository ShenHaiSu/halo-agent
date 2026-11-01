using System.Reflection;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>上下文压缩：保留尾部完整回合、追加标记落盘、失败不变、并发互斥与对账校验。</summary>
public sealed class CompactTests
{
    [Fact]
    public async Task Compact_keeps_tail_turns_and_history_verbatim()
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

        Assert.True(removed > 0, "压缩应折叠旧消息进摘要");
        Assert.Equal("这是摘要", summary);

        // 内存中尾部保留的完整回合依然逐字完全一致
        var after = f.MemoryMessages().TakeLast(4).Select(m => m.Content).ToArray();
        Assert.Equal(tail, after);

        f.AssertInvariants(f.MemoryMessages());
    }

    [Fact]
    public async Task Compact_failure_leaves_history_untouched()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 3; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        var before = f.MemoryMessages().Select(m => m.RoleWire + "|" + m.Content).ToArray();
        string? path = f.Store.HistoryPath;
        long lenBefore = path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        f.Llm.EnqueueThrow(new HttpRequestException("boom"));

        await Assert.ThrowsAnyAsync<Exception>(() => f.Session.CompactAsync(CancellationToken.None));

        var after = f.MemoryMessages().Select(m => m.RoleWire + "|" + m.Content).ToArray();
        Assert.Equal(before, after);
        // 失败时文件保持原状
        if (path is not null && File.Exists(path))
            Assert.Equal(lenBefore, new FileInfo(path).Length);

        f.AssertInvariants(f.MemoryMessages());
    }

    private static string[] ReadLinesShared(string path)
    {
        var list = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null) list.Add(line);
        return list.ToArray();
    }

    private static string ReadTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public async Task Compact_appends_marker_and_preserves_history()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 6; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        string? path = f.Store.HistoryPath;
        Assert.NotNull(path);
        int linesBefore = ReadLinesShared(path!).Length;
        int memBefore = f.MemoryMessages().Count;
        int snapBefore = f.Session.BuildSnapshot().Turns.Count;

        f.Llm.EnqueueContent("这是要点浓缩");
        var r = await f.Session.CompactAsync(CancellationToken.None);

        Assert.True(r.Removed > 0);
        Assert.Equal(memBefore, r.TotalBefore);
        // 单向追加了一行 marker，总条数 +1
        Assert.Equal(memBefore + 1, r.TotalAfter);

        int linesAfter = ReadLinesShared(path!).Length;
        // 文件恢复 Append-Only，增加 1 行 marker
        Assert.Equal(linesBefore + 1, linesAfter);

        // 文件回读 == 内存消息数（除 system）
        var (_, fileMsgs, _) = SessionStore.ReadHistory(path!);
        Assert.Equal(f.MemoryMessages().Count(m => m.RoleWire != "system"), fileMsgs.Count);

        // 历史回合全部无损保留
        var snapshot = f.Session.BuildSnapshot();
        Assert.Equal(snapBefore, snapshot.Turns.Count);

        // 第 5 回合前面挂上了 CompactDivider
        Assert.NotNull(snapshot.Turns[4].DividerBefore);
        Assert.Equal("这是要点浓缩", snapshot.Turns[4].DividerBefore!.Summary);
        Assert.Equal(1, snapshot.Turns[4].DividerBefore!.Seq);
        Assert.Equal(4, snapshot.Turns[4].DividerBefore!.RemovedTurns);
        Assert.Equal(2, snapshot.Turns[4].DividerBefore!.KeptTurns);

        // 文件中包含 compact marker 字段
        Assert.Contains("\"marker\":\"compact\"", ReadTextShared(path!));

        // 诊断对账字段
        Assert.Equal(r.Removed, f.Session.LastCompactRemoved);
        Assert.Equal(r.TotalBefore, f.Session.LastCompactBefore);
        Assert.Equal(r.TotalAfter, f.Session.LastCompactAfter);
        Assert.True(f.Session.LastCompactTsMs > 0);
    }

    [Fact]
    public async Task Compact_noop_returns_zero_with_notice()
    {
        using var f = new AgentFixture();
        f.Llm.EnqueueContent("回复0");
        await f.Session.RunTurnAsync("第0条", CancellationToken.None);

        var r = await f.Session.CompactAsync(CancellationToken.None);
        Assert.Equal(0, r.Removed);
        Assert.Equal(r.TotalBefore, r.TotalAfter);
        await f.WaitForKindAsync("notice", 5000);
        Assert.Contains(f.EventTrace(), k => k == "notice");
    }

    [Fact]
    public async Task Compact_rejected_when_busy_and_prompt_rejected_when_compacting()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 6; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        var busyField = typeof(HaloAgent.Engine.Core.Agent.AgentSession)
            .GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        busyField.SetValue(f.Session, true);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.CompactAsync(CancellationToken.None));
        }
        finally
        {
            busyField.SetValue(f.Session, false);
        }

        var compactingField = typeof(HaloAgent.Engine.Core.Agent.AgentSession)
            .GetField("_compacting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        compactingField.SetValue(f.Session, true);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.RunTurnAsync("hi", CancellationToken.None));
        }
        finally
        {
            compactingField.SetValue(f.Session, false);
        }
    }
}
