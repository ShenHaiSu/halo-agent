using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>历史孤儿只读投影：合成取消轮、顺序重排与各类边界的回归。</summary>
public sealed class HistoryTurnProjectorTests
{
    private static TurnSummary Turn(string id, long seq = 1) =>
        new()
        {
            TurnId = id,
            TurnSeq = seq,
            UserMessageId = id.StartsWith("uturn-", StringComparison.OrdinalIgnoreCase)
                ? id.Substring(6)
                : null
        };

    private static HistoryUserTurn Orphan(string id, long seq) =>
        new(id, "user-content-" + id, "2026-11-26T00:00:00.0000000Z", seq);

    [Fact]
    public void MissingHistoricalUserTurn_ProjectsCancelledSummary()
    {
        var refs = new List<HistoryMessageRef> { new("user-old", "user", null) };
        var missing = new List<HistoryUserTurn> { Orphan("user-old", 1) };

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            new List<TurnSummary>(),
            refs,
            missing,
            hasActiveAgentTurn: false
        );

        var turn = Assert.Single(result);
        Assert.Equal("uturn-user-old", turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal(string.Empty, turn.LastRequestId);
        Assert.Equal("turn", turn.Kind);
        Assert.Equal("user-content-user-old", turn.UserContent);
        Assert.Equal("user-old", turn.UserMessageId);
        Assert.Null(turn.AssistantMessageId);
        Assert.Equal(0, turn.Blocks);
        Assert.Equal(0, turn.Tools);
        Assert.Equal(1, turn.TurnSeq);
    }

    [Fact]
    public void MultipleOrphansInsertInMessageOrderAndRenumberSequentially()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("user-a", "user", null),
            new("user-b", "user", null),
            new("assistant-b", "assistant", null),
            new("user-c", "user", null),
        };
        var summaries = new List<TurnSummary> { Turn("uturn-user-b", 5) };
        var missing = new List<HistoryUserTurn> { Orphan("user-a", 1), Orphan("user-c", 4), };

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            summaries,
            refs,
            missing,
            hasActiveAgentTurn: false
        );

        Assert.Equal(
            new[] { "uturn-user-a", "uturn-user-b", "uturn-user-c" },
            result.Select(t => t.TurnId).ToArray()
        );
        Assert.Equal(new long[] { 1, 2, 3 }, result.Select(t => t.TurnSeq).ToArray());
    }

    [Fact]
    public void ExistingSummaryIsNotDuplicatedWhenOrphanPresent()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("user-old", "user", null),
            new("user-current", "user", null),
        };
        var summaries = new List<TurnSummary> { Turn("uturn-user-current", 9) };
        var missing = new List<HistoryUserTurn> { Orphan("user-old", 1) };

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            summaries,
            refs,
            missing,
            hasActiveAgentTurn: false
        );

        Assert.Equal(2, result.Count);
        Assert.Single(result, t => t.TurnId == "uturn-user-current");
        Assert.Single(result, t => t.TurnId == "uturn-user-old");
    }

    [Fact]
    public void ActiveTurnSkipsLatestUserButProjectsEarlierOrphan()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("user-old", "user", null),
            new("user-latest", "user", null),
        };
        var missing = new List<HistoryUserTurn>
        {
            Orphan("user-old", 1),
            Orphan("user-latest", 2),
        };

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            new List<TurnSummary>(),
            refs,
            missing,
            hasActiveAgentTurn: true
        );

        Assert.Equal("uturn-user-old", Assert.Single(result).TurnId);
    }

    [Fact]
    public void CompactStubRefIsNotTreatedAsMissingUser()
    {
        var represented = HistoryTurnProjector.CollectRepresentedUserIds(
            new List<TurnSummary> { Turn("uturn-user-current") }
        );
        var refs = new List<HistoryMessageRef>
        {
            new("compact-stub", "user", "compact_stub"),
            new("user-current", "user", null),
            new("orphan-user", "user", null),
        };

        var missing = HistoryTurnProjector.FindMissingUserRefs(refs, represented);

        Assert.DoesNotContain(missing, r => r.Id == "compact-stub");
        Assert.DoesNotContain(missing, r => r.Id == "user-current");
        Assert.Contains(missing, r => r.Id == "orphan-user");
    }

    [Fact]
    public void BranchSuffixTurnIdIsCollectedAsRepresentedUser()
    {
        var represented = HistoryTurnProjector.CollectRepresentedUserIds(
            new List<TurnSummary> { Turn("uturn-user-a:branch:sess-branch") }
        );
        Assert.Contains("user-a", represented);

        var missing = HistoryTurnProjector.FindMissingUserRefs(
            new List<HistoryMessageRef> { new("user-a", "user", null) },
            represented
        );
        Assert.Empty(missing);
    }

    [Fact]
    public void UnlocatableSummarySortedAfterLocatedItems()
    {
        var refs = new List<HistoryMessageRef> { new("user-old", "user", null) };
        var summaries = new List<TurnSummary> { Turn("unknown-turn", 1), };
        var missing = new List<HistoryUserTurn> { Orphan("user-old", 1) };

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            summaries,
            refs,
            missing,
            hasActiveAgentTurn: false
        );

        Assert.Equal("uturn-user-old", result[0].TurnId);
        Assert.Equal("unknown-turn", result[1].TurnId);
        Assert.Equal(new long[] { 1, 2 }, result.Select(t => t.TurnSeq).ToArray());
    }

    [Fact]
    public void RenumberingOnlyTouchesInMemoryDtoNotTurnId()
    {
        var refs = new List<HistoryMessageRef> { new("user-old", "user", null) };
        var existing = Turn("uturn-user-old", 77);

        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            new List<TurnSummary> { existing },
            refs,
            new List<HistoryUserTurn>(),
            false
        );

        Assert.Equal("uturn-user-old", result[0].TurnId);
        Assert.Equal(1, result[0].TurnSeq);
    }

    [Fact]
    public void FindMissingUserRefsIgnoresRoleCase()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("user-old", "USER", null),
            new("assistant-x", "Assistant", null),
        };

        var missing = HistoryTurnProjector.FindMissingUserRefs(
            refs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );

        Assert.Equal("user-old", Assert.Single(missing).Id);
    }

    [Fact]
    public void EmptyInputsReturnEmptyProjection()
    {
        var result = HistoryTurnProjector.ProjectMissingUserTurns(
            new List<TurnSummary>(),
            new List<HistoryMessageRef>(),
            new List<HistoryUserTurn>(),
            false
        );
        Assert.Empty(result);
    }
}
