#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;
using Host.Desktop.Services.Context;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007-M2：活跃切片纯函数验收（B1–B10）。</summary>
public sealed class ActiveContextSliceTests
{
    private static UnifiedChatMessage User(string c) => UnifiedChatMessage.User(c);

    private static UnifiedChatMessage Assistant(string c) => UnifiedChatMessage.Assistant(c);

    private static UnifiedChatMessage System(string c) => UnifiedChatMessage.System(c);

    private static UnifiedChatMessage Tool(string callId, string output) =>
        UnifiedChatMessage.Tool(callId, output);

    private static UnifiedChatMessage Stub(string summary = "摘要正文")
    {
        var m = UnifiedChatMessage.User(summary);
        m.Kind = MessageKind.CompactStub;
        return m;
    }

    private static void AssertSameIds(
        List<UnifiedChatMessage> expected,
        List<UnifiedChatMessage> actual
    )
    {
        Assert.Equal(expected.Select(m => m.Id).ToArray(), actual.Select(m => m.Id).ToArray());
    }

    [Fact(DisplayName = "P007-M2-B1: 空列表→空切片，起点 0")]
    public void Empty_Slice_Is_Empty()
    {
        Assert.Empty(ActiveContext.Slice(new List<UnifiedChatMessage>()));
        Assert.Empty(ActiveContext.Slice(null));
        Assert.Equal(0, ActiveContext.ResolveStartIndex(null));
        Assert.Equal(-1, ActiveContext.FindLastStubIndex(null));
    }

    [Fact(DisplayName = "P007-M2-B2: 无桩→全量恒等")]
    public void NoStub_Is_Identity()
    {
        var all = new List<UnifiedChatMessage> { User("q1"), Assistant("a1"), User("q2") };
        var slice = ActiveContext.Slice(all);
        AssertSameIds(all, slice);
        Assert.Equal(0, ActiveContext.ResolveStartIndex(all));
        Assert.Equal(-1, ActiveContext.FindLastStubIndex(all));
    }

    [Fact(DisplayName = "P007-M2-B3: 桩在最末→仅桩")]
    public void StubAtEnd_Returns_Only_Stub()
    {
        var stub = Stub();
        var all = new List<UnifiedChatMessage> { User("q"), Assistant("a"), stub };
        AssertSameIds(new List<UnifiedChatMessage> { stub }, ActiveContext.Slice(all));
        Assert.Equal(2, ActiveContext.FindLastStubIndex(all));
    }

    [Fact(DisplayName = "P007-M2-B4: 桩+后续→桩及之后全进，桩前全丢")]
    public void StubPlusTail_Drops_PreStub()
    {
        var stub = Stub();
        var u2 = User("q2");
        var a2 = Assistant("a2");
        var all = new List<UnifiedChatMessage> { User("q1"), Assistant("a1"), stub, u2, a2 };
        AssertSameIds(new List<UnifiedChatMessage> { stub, u2, a2 }, ActiveContext.Slice(all));
    }

    [Fact(DisplayName = "P007-M2-B5: 双桩→只认最后一个")]
    public void TwoStubs_Keeps_Last_Only()
    {
        var stubA = Stub("摘要A");
        var stubB = Stub("摘要B");
        var u3 = User("q3");
        var all = new List<UnifiedChatMessage> { User("q1"), stubA, User("q2"), stubB, u3 };
        AssertSameIds(new List<UnifiedChatMessage> { stubB, u3 }, ActiveContext.Slice(all));
        Assert.Equal(3, ActiveContext.FindLastStubIndex(all));
    }

    [Fact(DisplayName = "P007-M2-B6: 前导 system 保留且在首位")]
    public void LeadingSystem_Is_Kept_First()
    {
        var sys = System("sys");
        var stub = Stub();
        var u2 = User("q2");
        var all = new List<UnifiedChatMessage> { sys, User("q1"), stub, u2 };
        AssertSameIds(new List<UnifiedChatMessage> { sys, stub, u2 }, ActiveContext.Slice(all));
    }

    [Fact(DisplayName = "P007-M2-B7: 桩后仅 tool 行→仍切片，由上层 heal 处理")]
    public void ToolOnly_After_Stub_Does_Not_Throw()
    {
        var stub = Stub();
        var tool = Tool("call_1", "out");
        var all = new List<UnifiedChatMessage> { User("q1"), stub, tool };
        var slice = ActiveContext.Slice(all);
        Assert.Equal(2, slice.Count);
        Assert.Same(stub, slice[0]);
        Assert.Same(tool, slice[1]);
    }

    [Fact(DisplayName = "P007-M2-B8: 桩在 index 0→回退全量")]
    public void StubAtZero_Falls_Back_To_Full()
    {
        var stub = Stub();
        var u1 = User("q1");
        var all = new List<UnifiedChatMessage> { stub, u1 };
        AssertSameIds(all, ActiveContext.Slice(all));
        Assert.Equal(0, ActiveContext.ResolveStartIndex(all));
    }

    [Fact(DisplayName = "P007-M2-B9: 前导 system 越过桩（脏数据）→回退全量")]
    public void SystemAfterStub_Falls_Back_To_Full()
    {
        var all = new List<UnifiedChatMessage> { Stub(), System("sys"), User("q1") };
        Assert.Equal(0, ActiveContext.ResolveStartIndex(all));
        AssertSameIds(all, ActiveContext.Slice(all));
    }

    [Fact(DisplayName = "P007-M2-B10: 压缩准入理由 EMPTY/TOO_SHORT/NO_NEW_CONTENT/可压")]
    public void PrepareCompactionReason_Matrix()
    {
        Assert.Equal("EMPTY", ActiveContext.PrepareCompactionReason(null));
        Assert.Equal(
            "EMPTY",
            ActiveContext.PrepareCompactionReason(new List<UnifiedChatMessage>())
        );
        // 无桩短历史
        Assert.Equal(
            "TOO_SHORT",
            ActiveContext.PrepareCompactionReason(
                new List<UnifiedChatMessage> { User("q1"), Assistant("a1") }
            )
        );
        // 无桩长历史可压
        var longNoStub = Enumerable
            .Range(0, 8)
            .Select(i => User("q" + i))
            .ToList<UnifiedChatMessage>();
        Assert.Equal(string.Empty, ActiveContext.PrepareCompactionReason(longNoStub));
        // 有桩但桩后无新内容
        var sysOnly = new List<UnifiedChatMessage> { System("sys"), Stub() };
        Assert.Equal("NO_NEW_CONTENT", ActiveContext.PrepareCompactionReason(sysOnly));
        // 有桩桩后太短
        Assert.Equal(
            "TOO_SHORT",
            ActiveContext.PrepareCompactionReason(
                new List<UnifiedChatMessage> { Stub(), User("q1") }
            )
        );
        // 有桩桩后足够长可压
        var ready = new List<UnifiedChatMessage> { System("sys"), Stub() };
        ready.AddRange(Enumerable.Range(0, 7).Select(i => User("n" + i)));
        Assert.Equal(string.Empty, ActiveContext.PrepareCompactionReason(ready));
    }
}
