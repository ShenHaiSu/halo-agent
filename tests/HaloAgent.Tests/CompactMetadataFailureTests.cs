using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Host.Desktop.Services.Context;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactMetadataFailureTests
{
    [Fact]
    public async Task Metadata_Failure_Does_Not_Turn_Committed_Stub_Into_Failure()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        var service = fixture.CreateMetadataFailingService();

        var result = await new CompactionService(
            service,
            fixture.Budget,
            new RecordingCompactGateway()
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Completed, result.Outcome);
        Assert.True(result.Success);
        Assert.Contains("COMPACT_METADATA_WARNING", result.MetadataWarning);
        Assert.NotNull(result.StubMessageId);
        var messages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        Assert.True(ActiveContext.IsStub(messages[^1]));
    }

    [Fact]
    public async Task Existing_Stub_Forces_Stub_Mode_When_Metadata_Was_Not_Frozen()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        var service = fixture.CreateMetadataFailingService();
        var compaction = new CompactionService(
            service,
            fixture.Budget,
            new RecordingCompactGateway()
        );
        Assert.True((await compaction.CompactAsync(workspaceId, sessionId)).Success);
        fixture.SetCompactMode(CompactMode.Legacy);
        for (int i = 0; i < 6; i++)
            await fixture.Service.AppendSessionMessageAsync(
                workspaceId,
                sessionId,
                UnifiedChatMessage.User($"after-metadata-fault-{i}")
            );

        var second = await compaction.CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Completed, second.Outcome);
        Assert.NotNull(second.StubMessageId);
        Assert.Equal(
            2,
            (await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId)).Count(message =>
                ActiveContext.IsStub(message)
            )
        );
    }
}
