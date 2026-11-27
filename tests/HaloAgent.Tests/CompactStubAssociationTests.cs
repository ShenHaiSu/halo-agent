using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactStubAssociationTests
{
    [Fact]
    public async Task Latest_Stub_Does_Not_Borrow_An_Older_Turn_Id()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        var store = fixture.CreateStore();
        var stubA = UnifiedChatMessage.User("stub-a-summary");
        stubA.Kind = MessageKind.CompactStub;
        await store.InsertCompactStubAsync(
            workspaceId,
            sessionId,
            stubA,
            CreateTurn(stubA, "stub-a-summary"),
            default
        );
        var stubB = UnifiedChatMessage.User("stub-b-summary");
        stubB.Kind = MessageKind.CompactStub;
        stubB.MetaJson = JsonSerializer.Serialize(
            new CompactStubMeta { V = 1, RemovedCount = 7 },
            HaloJsonOptions.CompactCamelCase
        );
        await fixture.Repository.AppendMessageAsync(workspaceId, sessionId, stubB);

        var info = await store.GetLastCompactStubAsync(workspaceId, sessionId);

        Assert.NotNull(info);
        Assert.Equal(stubB.Id, info!.MessageId);
        Assert.False(info.HasMatchingTurn);
        Assert.Equal(string.Empty, info.TurnId);
        Assert.Equal(7, info.RemovedCount);
    }

    [Fact]
    public void Metadata_V1_Missing_Fields_Remain_Unknown()
    {
        var meta = JsonSerializer.Deserialize<CompactStubMeta>(
            "{\"v\":1,\"removedCount\":9,\"fromSeq\":10,\"toSeq\":20}",
            HaloJsonOptions.CompactCamelCase
        );

        Assert.NotNull(meta);
        Assert.Equal(1, meta!.V);
        Assert.Equal(9, meta.RemovedCount);
        Assert.Null(meta.CoveredMessageCount);
        Assert.Null(meta.SummarizedMessageCount);
        Assert.Null(meta.ExcludedMessageCount);
        Assert.Null(meta.TruncatedMessageCount);
        Assert.Null(meta.SummaryChunkCount);
        Assert.Null(meta.SummaryStrategy);
        Assert.Null(meta.CompactLlmCallCount);
    }

    [Fact]
    public void Result_Factories_Set_Explicit_Outcome()
    {
        Assert.Equal(CompactionOutcome.Completed, CompactionResult.Ok(1, 2, "", null).Outcome);
        Assert.Equal(
            CompactionOutcome.Completed,
            CompactionResult.OkStub(1, 2, "stub", "turn", 3, "").Outcome
        );
        var noOp = CompactionResult.NoOp(CompactionNoOpReasons.NoNewContent);
        Assert.Equal(CompactionOutcome.NoOp, noOp.Outcome);
        Assert.True(noOp.Success);
        Assert.Equal(CompactionNoOpReasons.NoNewContent, noOp.NoOpReason);
        Assert.Equal(CompactionOutcome.Failed, CompactionResult.Fail("CODE", "message").Outcome);
    }

    private static TurnSummary CreateTurn(UnifiedChatMessage stub, string summary) =>
        new()
        {
            TurnId = "cturn-" + stub.Id,
            Kind = TurnKind.Compact,
            IterEnd = 1,
            UserPreview = "上下文压缩",
            UserContent = summary,
            Blocks = 0,
            FinalBlockId = null,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
}
