using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

public sealed class MessageAppendFailureTests
{
    [Fact]
    public async Task Message_Insert_Failure_Propagates_Without_Metadata_Update()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.Service.AppendSessionMessageAsync(
            workspaceId,
            sessionId,
            UnifiedChatMessage.User("before-failure")
        );
        var before = await fixture.Service.GetSessionAsync(workspaceId, sessionId);
        await fixture.ExecuteSqlAsync(
            workspaceId,
            "CREATE TRIGGER fail_message_insert BEFORE INSERT ON messages"
                + " BEGIN SELECT RAISE(ABORT, 'message-insert-fault'); END;"
        );

        await Assert.ThrowsAsync<SqliteException>(
            () =>
                fixture.Service.AppendSessionMessageAsync(
                    workspaceId,
                    sessionId,
                    UnifiedChatMessage.User("must-not-persist")
                )
        );

        var after = await fixture.Service.GetSessionAsync(workspaceId, sessionId);
        Assert.Equal(before!.MessageCount, after!.MessageCount);
        Assert.Equal(before.TotalInputTokens, after.TotalInputTokens);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        var messages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        Assert.Single(messages);
        Assert.Equal("before-failure", messages[0].Content);
    }
}
