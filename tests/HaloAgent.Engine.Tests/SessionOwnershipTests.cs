using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Ownership handover: the engine persists sessions, serves paged history with
/// truncated previews, carries attachment references without bytes, and routes
/// replay, branch retry and rollback through the new chain.
/// </summary>
public sealed class SessionOwnershipTests
{
    private static async Task<string> NewSessionAsync(FrameServer server, string title = "owned")
    {
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(300, "session.new", new { title })
        );
        Assert.True(created.Ok);
        return created.Result!.Value.GetProperty("sessionId").GetString()!;
    }

    [Fact]
    public async Task HistoryPageMarksTruncatedPreview()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        string longText = new string('x', 5000);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(301, "agent.prompt", new { sessionId, text = longText })
        );
        Assert.True(prompted.Ok);
        var page = await server.SubmitAsync(
            RequestFrame.WithPayload(302, "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        var items = page.Result!.Value.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 2);
        var preview = items.First(e => e.GetProperty("role").GetString() == "assistant");
        Assert.True(preview.GetProperty("truncated").GetBoolean());
        Assert.True(preview.GetProperty("text").GetString()!.Length <= 2048);
    }

    [Fact]
    public async Task PromptWithAttachmentsRegistersReferences()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(
                310,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "look at this",
                    attachments = new[]
                    {
                        new
                        {
                            digest = "digest-shot",
                            fileName = "shot.png",
                            byteSize = 42L
                        },
                    }
                }
            )
        );
        Assert.True(prompted.Ok);
        Assert.True(server.Attachments.TryLookup("digest-shot", out var reference));
        Assert.Equal("attachments/digest-shot-shot.png", reference!.RelativePath);
        var batch = server.ReadEvents(0, 500);
        var started = batch.Frames.First(f => f.Kind == "tool.start");
        string raw = started.Payload.GetRawText();
        Assert.Contains("digest-shot", raw);
    }

    [Fact]
    public async Task RetryReplaysLastUserTurnAfterDone()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(320, "agent.prompt", new { sessionId, text = "again" })
        );
        Assert.True(prompted.Ok);
        string firstTurn = prompted.Result!.Value.GetProperty("turnId").GetString()!;
        var retried = await server.SubmitAsync(
            RequestFrame.WithPayload(321, "agent.retry", new { sessionId })
        );
        Assert.True(retried.Ok);
        var batch = server.ReadEvents(0, 500);
        var replay = batch.Frames.FirstOrDefault(f =>
            f.Kind == "turn.started"
            && f.Payload.TryGetProperty("retriedFrom", out var source)
            && source.GetString() == firstTurn
        );
        Assert.NotNull(replay);
    }

    [Fact]
    public async Task UndoRollbackRemovesLastMessageAndNotifies()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        await server.SubmitAsync(
            RequestFrame.WithPayload(330, "agent.prompt", new { sessionId, text = "keep me" })
        );
        int before = server.Sessions.Count(sessionId);
        var rolled = await server.SubmitAsync(
            RequestFrame.WithPayload(331, "tool.undoRollback", new { sessionId })
        );
        Assert.True(rolled.Ok);
        // Rollback removes the newest turn as a whole (user plus assistant row).
        Assert.Equal(before - 2, server.Sessions.Count(sessionId));
        Assert.Equal(2, rolled.Result!.Value.GetProperty("removedCount").GetInt32());
        Assert.Equal(2, rolled.Result!.Value.GetProperty("removedMessageIds").GetArrayLength());
        var batch = server.ReadEvents(0, 500);
        Assert.Contains(batch.Frames, f => f.Kind == "session.updated");
    }

    [Fact]
    public async Task UndoRollbackWithoutHistoryReportsNotFound()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        var rolled = await server.SubmitAsync(
            RequestFrame.WithPayload(332, "tool.undoRollback", new { sessionId })
        );
        Assert.False(rolled.Ok);
        Assert.Equal("NOT_FOUND", rolled.Error!.Code);
    }

    [Fact]
    public async Task SaveLoadAndRollbackRoundTrip()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-owned-" + Guid.NewGuid().ToString("N")
        );
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, "durable");
        await server.SubmitAsync(
            RequestFrame.WithPayload(340, "agent.prompt", new { sessionId, text = "persist me" })
        );
        server.SaveSessions(directory);

        await using var revived = new FrameServer();
        revived.LoadSessions(directory);
        var page = await revived.SubmitAsync(
            RequestFrame.WithPayload(341, "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        Assert.True(page.Result!.Value.GetProperty("items").GetArrayLength() >= 2);

        await server.SubmitAsync(
            RequestFrame.WithPayload(342, "agent.prompt", new { sessionId, text = "second" })
        );
        server.SaveSessions(directory);
        Assert.True(server.RollbackSession(directory, sessionId));
        var restored = await server.SubmitAsync(
            RequestFrame.WithPayload(343, "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(restored.Ok);
    }
}
