using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Sunk engine behavior: turn truth, reentry guard, confirmation gate and compaction guard.
/// </summary>
public sealed class TurnLifecycleTests
{
    private static async Task<string> NewSessionAsync(FrameServer server)
    {
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(100, "session.new", new { title = "ledger" })
        );
        Assert.True(created.Ok);
        return created.Result!.Value.GetProperty("sessionId").GetString()!;
    }

    [Fact]
    public async Task PromptThenHistoryRoundTrip()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(101, "agent.prompt", new { sessionId, text = "hello" })
        );
        Assert.True(answered.Ok);
        var page = await server.SubmitAsync(
            RequestFrame.WithPayload(102, "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        int count = page.Result!.Value.GetProperty("items").GetArrayLength();
        Assert.True(count >= 2);
    }

    [Fact]
    public async Task SecondPromptWhileActiveReportsInProgress()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(
                110,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "first",
                    needsConfirm = true
                }
            )
        );
        Assert.True(first.Ok);
        var second = await server.SubmitAsync(
            RequestFrame.WithPayload(111, "agent.prompt", new { sessionId, text = "second" })
        );
        Assert.False(second.Ok);
        Assert.Equal("TURN_IN_PROGRESS", second.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(second.Error.Hint));
    }

    [Fact]
    public async Task ConfirmationGateResolvesThroughAnswer()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(
                120,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "needs review",
                    needsConfirm = true
                }
            )
        );
        Assert.True(prompted.Ok);
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;
        var answer = await server.SubmitAsync(
            RequestFrame.WithPayload(121, "agent.answer", new { turnId, approved = true })
        );
        Assert.True(answer.Ok);
        var batch = server.ReadEvents(0, 500);
        Assert.Contains(batch.Frames, f => f.Kind == "tool.confirm");
        Assert.Contains(batch.Frames, f => f.Kind == "turn.done");
    }

    [Fact]
    public async Task RejectedAnswerCancelsTurn()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(
                130,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "risky",
                    needsConfirm = true
                }
            )
        );
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;
        var answer = await server.SubmitAsync(
            RequestFrame.WithPayload(
                131,
                "agent.answer",
                new
                {
                    turnId,
                    approved = false,
                    rejected = true
                }
            )
        );
        Assert.True(answer.Ok);
        var batch = server.ReadEvents(0, 500);
        Assert.Contains(batch.Frames, f => f.Kind == "turn.cancelled");
    }

    [Fact]
    public async Task CompactingSessionRejectsPrompt()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        Assert.True(server.Sessions.TryBeginCompaction(sessionId));
        try
        {
            var prompted = await server.SubmitAsync(
                RequestFrame.WithPayload(140, "agent.prompt", new { sessionId, text = "hi" })
            );
            Assert.False(prompted.Ok);
            Assert.Equal("COMPACTING", prompted.Error!.Code);
        }
        finally
        {
            server.Sessions.EndCompaction(sessionId, "summary");
        }
        var after = await server.SubmitAsync(
            RequestFrame.WithPayload(141, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(after.Ok);
    }

    [Fact]
    public async Task RetryReplaysLastUserMessage()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(150, "agent.prompt", new { sessionId, text = "again" })
        );
        Assert.True(first.Ok);
        var retry = await server.SubmitAsync(
            RequestFrame.WithPayload(151, "agent.retry", new { sessionId })
        );
        Assert.True(retry.Ok);
    }

    [Fact]
    public async Task SurplusFieldsStayCompatible()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(
                160,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "hi",
                    futureFlag = true,
                    extra = 42
                }
            )
        );
        Assert.True(answered.Ok);
    }
}
