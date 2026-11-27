using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Services.Context;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactSummaryCoverageTests
{
    [Fact]
    public async Task Stub_Summary_Covers_All_Twenty_Rounds_And_Writes_V2_Metadata()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 20, "coverage-round");
        fixture.SetCompactMode(CompactMode.Stub);
        var gateway = new RecordingCompactGateway();

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            gateway
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Completed, result.Outcome);
        Assert.Single(gateway.Requests);
        string body = gateway.Requests[0].Messages[0].Content!;
        for (int i = 0; i < 20; i++)
            Assert.Contains($"coverage-round-{i:00}", body);
        var active = await fixture.Service.GetActiveContextMessagesAsync(workspaceId, sessionId);
        Assert.Equal(2, active.Count);
        Assert.True(ActiveContext.IsStub(active[^1]));
        var messages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        var stub = messages[^1];
        var meta = JsonSerializer.Deserialize<CompactStubMeta>(
            stub.MetaJson!,
            HaloJsonOptions.CompactCamelCase
        )!;
        Assert.Equal(2, meta.V);
        Assert.Equal(40, meta.RemovedCount);
        Assert.Equal(40, meta.CoveredMessageCount);
        Assert.Equal(40, meta.SummarizedMessageCount);
        Assert.Equal(0, meta.ExcludedMessageCount);
        Assert.Equal(0, meta.TruncatedMessageCount);
        Assert.Equal(meta.RemovedCount, meta.SummarizedMessageCount + meta.ExcludedMessageCount);
        Assert.Equal(1, meta.SummaryChunkCount);
        Assert.Equal("single", meta.SummaryStrategy);
        Assert.Equal(1, meta.CompactLlmCallCount);
        Assert.Equal(20, meta.SummaryTokens);
        Assert.Equal(100, meta.PromptTokens);
        Assert.Equal(5, meta.CachedTokens);
        Assert.Equal(messages[1].Id, meta.CoveredFromMessageId);
        Assert.Equal(messages[^2].Id, meta.CoveredToMessageId);
        Assert.NotNull(meta.FromSeq);
        Assert.NotNull(meta.ToSeq);
        Assert.True(meta.FromSeq < meta.ToSeq);
    }

    [Fact]
    public async Task Short_History_Is_NoOp_Without_Request_Or_Stub()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        for (int i = 0; i < 5; i++)
            await fixture.Service.AppendSessionMessageAsync(
                workspaceId,
                sessionId,
                UnifiedChatMessage.User($"short-{i}")
            );
        fixture.SetCompactMode(CompactMode.Stub);
        var gateway = new RecordingCompactGateway();

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            gateway
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.NoOp, result.Outcome);
        Assert.True(result.Success);
        Assert.Equal(CompactionNoOpReasons.TooShort, result.NoOpReason);
        Assert.Empty(gateway.Requests);
        Assert.Empty(
            (await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId)).Where(message =>
                ActiveContext.IsStub(message)
            )
        );
    }

    [Fact]
    public async Task Summary_Failure_Does_Not_Change_Active_Context()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        var before = await fixture.Service.GetActiveContextMessagesAsync(workspaceId, sessionId);
        var gateway = new RecordingCompactGateway((_, _, _) => ThrowSummary());

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            gateway
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Failed, result.Outcome);
        Assert.Equal("COMPACT_LLM_ERROR", result.ErrorCode);
        var after = await fixture.Service.GetActiveContextMessagesAsync(workspaceId, sessionId);
        Assert.Equal(before.Select(message => message.Id), after.Select(message => message.Id));
        Assert.Null(result.StubMessageId);
    }

    [Fact]
    public async Task Second_Compact_Includes_Previous_Summary_And_All_New_History()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 4, "first-round");
        fixture.SetCompactMode(CompactMode.Stub);
        var gateway = new RecordingCompactGateway(
            (index, _, _) =>
                RecordingCompactGateway.Stream(
                    index == 0 ? "previous-summary-marker" : "second-summary",
                    100,
                    20,
                    5
                )
        );
        var compaction = new CompactionService(fixture.Service, fixture.Budget, gateway);
        Assert.True((await compaction.CompactAsync(workspaceId, sessionId)).Success);
        for (int i = 0; i < 8; i++)
            await fixture.Service.AppendSessionMessageAsync(
                workspaceId,
                sessionId,
                UnifiedChatMessage.User($"new-round-{i:00}")
            );

        var result = await compaction.CompactAsync(workspaceId, sessionId, keepLastGroups: 2);

        Assert.Equal(CompactionOutcome.Completed, result.Outcome);
        string secondBody = gateway.Requests[1].Messages[0].Content!;
        Assert.Contains("previous-summary-marker", secondBody);
        for (int i = 0; i < 8; i++)
            Assert.Contains($"new-round-{i:00}", secondBody);
    }

    [Fact]
    public async Task Cancelled_Summary_Does_Not_Write_Stub()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        var gateway = new RecordingCompactGateway((_, _, _) => CancelSummary());

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            gateway
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Failed, result.Outcome);
        Assert.Equal("COMPACT_SUMMARY_CANCELLED", result.ErrorCode);
        Assert.DoesNotContain(
            await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId),
            message => ActiveContext.IsStub(message)
        );
    }

    private static async IAsyncEnumerable<UnifiedStreamEvent> ThrowSummary()
    {
        await Task.Yield();
        throw new InvalidOperationException("summary-failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<UnifiedStreamEvent> CancelSummary()
    {
        await Task.Yield();
        yield return UnifiedStreamEvent.Cancelled();
    }
}
