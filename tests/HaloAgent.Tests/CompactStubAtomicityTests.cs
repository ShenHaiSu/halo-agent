using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Services.Context;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactStubAtomicityTests
{
    [Fact]
    public async Task Turn_Insert_Failure_Rolls_Back_Stub_Message_And_Metadata()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        var beforeMessages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        var beforeActive = await fixture.Service.GetActiveContextMessagesAsync(
            workspaceId,
            sessionId
        );
        var beforeMeta = await fixture.Service.GetSessionAsync(workspaceId, sessionId);
        await fixture.ExecuteSqlAsync(
            workspaceId,
            "CREATE TRIGGER fail_compact_turn BEFORE INSERT ON turns"
                + " WHEN NEW.kind = 'compact'"
                + " BEGIN SELECT RAISE(ABORT, 'compact-turn-fault'); END;"
        );

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            new RecordingCompactGateway()
        ).CompactAsync(workspaceId, sessionId);

        Assert.Equal(CompactionOutcome.Failed, result.Outcome);
        Assert.Equal("COMPACT_STUB_WRITE_ERROR", result.ErrorCode);
        var afterMessages = await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId);
        Assert.Equal(
            beforeMessages.Select(message => message.Id),
            afterMessages.Select(message => message.Id)
        );
        Assert.DoesNotContain(afterMessages, message => ActiveContext.IsStub(message));
        var afterActive = await fixture.Service.GetActiveContextMessagesAsync(
            workspaceId,
            sessionId
        );
        Assert.Equal(
            beforeActive.Select(message => message.Id),
            afterActive.Select(message => message.Id)
        );
        var afterMeta = await fixture.Service.GetSessionAsync(workspaceId, sessionId);
        Assert.Equal(beforeMeta!.MessageCount, afterMeta!.MessageCount);
        Assert.Equal(
            0,
            await fixture.QueryLongAsync(
                workspaceId,
                $"SELECT COUNT(*) FROM turns WHERE session_id = '{sessionId}' AND kind = 'compact';"
            )
        );
    }

    [Fact]
    public async Task Busy_Retry_Succeeds_On_Second_Attempt()
    {
        int attempts = 0;
        long result = await SessionHistoryStore.ExecuteWithBusyRetryAsync(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new SqliteException("database-busy", 5);
            return Task.FromResult(42L);
        });

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Non_Busy_Error_Is_Not_Retried()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<SqliteException>(
            () =>
                SessionHistoryStore.ExecuteWithBusyRetryAsync(_ =>
                {
                    attempts++;
                    throw new SqliteException("constraint-failure", 19);
                })
        );
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancelled_Retry_Loop_Does_Not_Start_Transaction()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () =>
                SessionHistoryStore.ExecuteWithBusyRetryAsync(
                    _ =>
                    {
                        attempts++;
                        return Task.FromResult(1L);
                    },
                    cancellation.Token
                )
        );
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Cancelled_Before_Commit_Does_Not_Write_Stub()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        await fixture.AppendHistoryAsync(workspaceId, sessionId, 6);
        fixture.SetCompactMode(CompactMode.Stub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new CompactionService(
            fixture.Service,
            fixture.Budget,
            new RecordingCompactGateway()
        ).CompactAsync(workspaceId, sessionId, ct: cancellation.Token);

        Assert.Equal(CompactionOutcome.Failed, result.Outcome);
        Assert.Equal("COMPACT_CANCELLED", result.ErrorCode);
        Assert.DoesNotContain(
            await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId),
            message => ActiveContext.IsStub(message)
        );
    }
}
