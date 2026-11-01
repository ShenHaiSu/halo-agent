 using System.Reflection;
 using HaloAgent.Engine.Core.Tests.TestSupport;
 using HaloAgent.Engine.Storage.Sessions;
 using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>S18/S19：上下文压缩（保尾 4 条逐字 + 失败不变）。P18：落盘同步 + 并发拒绝 + 文件原子性。</summary>
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
        string? path = f.Store.HistoryPath;
        long lenBefore = path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        f.Llm.EnqueueThrow(new HttpRequestException("boom"));

        await Assert.ThrowsAnyAsync<Exception>(() => f.Session.CompactAsync(CancellationToken.None));

        var after = f.MemoryMessages().Select(m => m.RoleWire + "|" + m.Content).ToArray();
        Assert.Equal(before, after);
        // P18：文件也不动
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
    public async Task P18_compact_rewrites_file_and_snapshot_shrinks()
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

        f.Llm.EnqueueContent("这是摘要");
        var r = await f.Session.CompactAsync(CancellationToken.None);

        Assert.True(r.Removed > 0);
        Assert.Equal(memBefore, r.TotalBefore);
        Assert.True(r.TotalAfter < r.TotalBefore);
        int linesAfter = ReadLinesShared(path!).Length;
        Assert.True(linesAfter < linesBefore);
        // 文件回读 == 内存（除 system）
        var (_, fileMsgs, _) = SessionStore.ReadHistory(path!);
        Assert.Equal(f.MemoryMessages().Count(m => m.RoleWire != "system"), fileMsgs.Count);
        Assert.True(f.Session.BuildSnapshot().Turns.Count <= snapBefore);
        // 摘要落盘：文件含摘要前缀
        Assert.Contains("上下文摘要", ReadTextShared(path!));
        // 诊断对账字段
        Assert.Equal(r.Removed, f.Session.LastCompactRemoved);
        Assert.Equal(r.TotalBefore, f.Session.LastCompactBefore);
        Assert.Equal(r.TotalAfter, f.Session.LastCompactAfter);
        Assert.True(f.Session.LastCompactTsMs > 0);
    }

    [Fact]
    public async Task P18_compact_noop_returns_zero_with_notice()
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
    public async Task P18_compact_rejected_when_busy_and_prompt_rejected_when_compacting()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 6; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        // busy 时压缩拒绝：用反射置 _busy
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

        // compacting 时开轮拒绝：用反射置 _compacting
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
