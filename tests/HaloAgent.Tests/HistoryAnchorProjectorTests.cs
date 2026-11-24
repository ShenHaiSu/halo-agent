using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>摘要消息锚点投影：展示 ID 与操作 ID 分离的跨层契约回归。</summary>
public sealed class HistoryAnchorProjectorTests
{
    private static List<HistoryMessageRef> StandardRefs() =>
        new()
        {
            new HistoryMessageRef("u1", "user", null),
            new HistoryMessageRef("a1", "assistant", null),
            new HistoryMessageRef("tool-a", "tool", null),
            new HistoryMessageRef("a2", "assistant", null),
            new HistoryMessageRef("u2", "user", null),
            new HistoryMessageRef("a3", "assistant", null),
        };

    private static TurnSummary Turn(string id, string? finalBlockId = null) =>
        new() { TurnId = id, FinalBlockId = finalBlockId };

    [Fact]
    public void SingleAssistantTurnProjectsBothAnchors()
    {
        var summaries = new List<TurnSummary> { Turn("uturn-u1") };
        HistoryAnchorProjector.Project(summaries, StandardRefs());
        Assert.Equal("u1", summaries[0].UserMessageId);
        Assert.Equal("a2", summaries[0].AssistantMessageId);
    }

    [Fact]
    public void ToolTurnUsesLastAssistantInWholeTurn()
    {
        var summaries = new List<TurnSummary> { Turn("uturn-u1") };
        HistoryAnchorProjector.Project(summaries, StandardRefs());
        Assert.Equal("u1", summaries[0].UserMessageId);
        Assert.Equal("a2", summaries[0].AssistantMessageId);
        Assert.DoesNotContain("tool-a", new[] { summaries[0].AssistantMessageId });
    }

    [Fact]
    public void FailedTurnWithoutAssistantLeavesAssistantAnchorNull()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("u1", "user", null),
            new("u2", "user", null),
        };
        var summaries = new List<TurnSummary> { Turn("uturn-u1") };
        HistoryAnchorProjector.Project(summaries, refs);
        Assert.Equal("u1", summaries[0].UserMessageId);
        Assert.Null(summaries[0].AssistantMessageId);
    }

    [Fact]
    public void BranchTurnIdStripsBranchSuffix()
    {
        var summaries = new List<TurnSummary> { Turn("uturn-u1:branch:sess_branch") };
        HistoryAnchorProjector.Project(summaries, StandardRefs());
        Assert.Equal("u1", summaries[0].UserMessageId);
        Assert.Equal("a2", summaries[0].AssistantMessageId);
    }

    [Fact]
    public void CompactTurnHasNoAssistantAnchor()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("msg_compact", "user", "compact_stub"),
            new("u2", "user", null),
        };
        var summaries = new List<TurnSummary> { Turn("cturn-msg_compact") };
        HistoryAnchorProjector.Project(summaries, refs);
        Assert.Equal("compact", summaries[0].Kind);
        Assert.Equal("msg_compact", summaries[0].UserMessageId);
        Assert.Null(summaries[0].AssistantMessageId);
    }

    [Fact]
    public void MissingCandidateClearsBothAnchors()
    {
        var summaries = new List<TurnSummary> { Turn("uturn-missing") };
        HistoryAnchorProjector.Project(summaries, StandardRefs());
        Assert.Null(summaries[0].UserMessageId);
        Assert.Null(summaries[0].AssistantMessageId);
    }

    [Fact]
    public void NonUserCandidateClearsBothAnchors()
    {
        var summaries = new List<TurnSummary> { Turn("uturn-a1") };
        HistoryAnchorProjector.Project(summaries, StandardRefs());
        Assert.Null(summaries[0].UserMessageId);
        Assert.Null(summaries[0].AssistantMessageId);
    }

    [Fact]
    public void RoleComparisonIgnoresCase()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("u1", "USER", null),
            new("a1", "Assistant", null),
        };
        var summaries = new List<TurnSummary> { Turn("uturn-u1") };
        HistoryAnchorProjector.Project(summaries, refs);
        Assert.Equal("u1", summaries[0].UserMessageId);
        Assert.Equal("a1", summaries[0].AssistantMessageId);
    }

    [Fact]
    public void TurnBoundaryFollowsMessageOrderNotSummaryOrder()
    {
        var refs = StandardRefs();
        var summaries = new List<TurnSummary> { Turn("uturn-u2"), Turn("uturn-u1") };
        HistoryAnchorProjector.Project(summaries, refs);
        Assert.Equal("u2", summaries[0].UserMessageId);
        Assert.Equal("a3", summaries[0].AssistantMessageId);
        Assert.Equal("u1", summaries[1].UserMessageId);
        Assert.Equal("a2", summaries[1].AssistantMessageId);
    }

    [Fact]
    public void LegacyCompactBlockNormalizesKind()
    {
        var refs = new List<HistoryMessageRef> { new("msg_compact", "user", null), };
        var summaries = new List<TurnSummary> { Turn("uturn-legacy", "compact:legacy") };
        HistoryAnchorProjector.Project(summaries, refs);
        Assert.Equal("compact", summaries[0].Kind);
        Assert.Null(summaries[0].AssistantMessageId);
    }
}
