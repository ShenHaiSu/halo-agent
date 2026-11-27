using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class LegacyCompactCompatibilityTests
{
    [Fact]
    public async Task New_Legacy_Compact_Writes_Compact_Turn_Without_Final_Block()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 10, "legacy-round");
        fixture.SetCompactMode(CompactMode.Legacy);

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            new RecordingCompactGateway()
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Completed, result.Outcome);
        var messages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        Assert.Equal(messages.Count, result.KeptMessages);
        Assert.Contains(
            messages,
            message =>
                string.Equals(message.Kind, MessageKind.Chat, StringComparison.Ordinal)
                && (message.Content ?? string.Empty).Contains("【上下文摘要")
        );
        Assert.DoesNotContain(
            messages,
            message =>
                string.Equals(message.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        var turns = await fixture.CreateStore().GetTimelineSummaryAsync(workspaceId, sessionId);
        var compact = Assert.Single(
            turns.Where(turn =>
                string.Equals(turn.Kind, TurnKind.Compact, StringComparison.Ordinal)
            )
        );
        Assert.Equal(0, compact.Blocks);
        Assert.Null(compact.FinalBlockId);
        Assert.Equal(result.RemovedMessages, compact.RemovedMessages);
    }

    [Fact]
    public async Task Legacy_Backfill_Normalizes_Only_Structured_Summary_Turn_And_Is_Idempotent()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        var store = fixture.CreateStore();
        var oldSummary = UnifiedChatMessage.User("【上下文摘要：旧数据】\nlegacy-summary");
        var oldTurn = new TurnSummary
        {
            TurnId = "uturn-" + oldSummary.Id,
            Kind = TurnKind.Turn,
            IterEnd = 1,
            UserPreview = "legacy",
            UserContent = oldSummary.Content!,
            FinalBlockId = "block-legacy-final",
            Blocks = 1,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        var oldBlock = new TimelineBlock
        {
            BlockId = oldTurn.FinalBlockId!,
            TurnId = oldTurn.TurnId,
            Iteration = 1,
            Sequence = 1,
            Kind = TimelineBlockKind.Text,
            Tag = TimelineBlockTag.Final,
            Content = oldSummary.Content!,
        };
        await store.AppendTurnAsync(
            workspaceId,
            sessionId,
            new[] { oldSummary },
            new[] { oldBlock },
            oldTurn
        );
        var normal = UnifiedChatMessage.User("normal-user-message");
        var normalTurn = new TurnSummary
        {
            TurnId = "uturn-" + normal.Id,
            Kind = TurnKind.Turn,
            IterEnd = 1,
            UserPreview = "normal",
            UserContent = normal.Content!,
            Blocks = 0,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        await store.AppendTurnAsync(
            workspaceId,
            sessionId,
            new[] { normal },
            Array.Empty<TimelineBlock>(),
            normalTurn
        );
        var database = new SessionHistoryDb(fixture.Storage);

        database.EnsureWorkspaceDatabase(workspaceId);
        database.EnsureWorkspaceDatabase(workspaceId);

        var messages = await store.GetMessagesAsync(workspaceId, sessionId);
        Assert.Equal(
            MessageKind.CompactStub,
            messages.Single(message => message.Id == oldSummary.Id).Kind
        );
        Assert.Null(messages.Single(message => message.Id == normal.Id).Kind);
        Assert.Equal(
            oldSummary.Content,
            messages.Single(message => message.Id == oldSummary.Id).Content
        );
        var turns = await store.GetTimelineSummaryAsync(workspaceId, sessionId);
        Assert.Equal(TurnKind.Compact, turns.Single(turn => turn.TurnId == oldTurn.TurnId).Kind);
        Assert.Equal(TurnKind.Turn, turns.Single(turn => turn.TurnId == normalTurn.TurnId).Kind);
        Assert.Equal(
            1,
            await fixture.QueryLongAsync(
                workspaceId,
                $"SELECT COUNT(*) FROM blocks WHERE session_id = '{sessionId}' AND block_id = '{oldTurn.FinalBlockId}';"
            )
        );
    }
}
