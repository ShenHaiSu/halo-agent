using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Persistence, usage, attachment and failure isolation for the sunk engine.
/// </summary>
public sealed class PersistenceAndLedgerTests
{
    [Fact]
    public async Task UsageQueryReflectsPromptTraffic()
    {
        await using var server = new FrameServer();
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(200, "session.new", new { title = "usage" })
        );
        string sessionId = created.Result!.Value.GetProperty("sessionId").GetString()!;
        var before = await server.SubmitAsync(RequestFrame.WithPayload(201, "usage.query"));
        long beforeInput = before.Result!.Value.GetProperty("inputTokens").GetInt64();
        await server.SubmitAsync(
            RequestFrame.WithPayload(202, "agent.prompt", new { sessionId, text = "count me" })
        );
        var after = await server.SubmitAsync(RequestFrame.WithPayload(203, "usage.query"));
        Assert.True(after.Result!.Value.GetProperty("inputTokens").GetInt64() > beforeInput);
        var cleared = await server.SubmitAsync(RequestFrame.WithPayload(204, "usage.clear"));
        Assert.True(cleared.Ok);
    }

    [Fact]
    public void AttachmentIndexDedupsByDigest()
    {
        var index = new AttachmentIndex();
        var first = index.Register("digest-one", "session-a", "shot.png", 12);
        var second = index.Register("digest-one", "session-a", "shot.png", 12);
        Assert.Equal(first.RelativePath, second.RelativePath);
        Assert.Equal(1, index.Count);
        Assert.True(index.TryLookup("digest-one", out _));
    }

    [Fact]
    public void HistoryFileRoundTripWithCorruptLine()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-history-" + Guid.NewGuid().ToString("N")
        );
        var ledger = new SessionLedger();
        ledger.EnsureSession("session-x", "demo");
        ledger.Append("session-x", "user", "hello", "turn-one");
        ledger.Append("session-x", "assistant", "world", "turn-one");
        JsonlHistoryFile.Save(directory, "session-x", "demo", ledger.Snapshot("session-x"));
        File.AppendAllText(Path.Combine(directory, "session-x.messages.jsonl"), "not-json{\n");
        Assert.True(
            JsonlHistoryFile.TryLoad(directory, "session-x", out string title, out var messages)
        );
        Assert.Equal("demo", title);
        Assert.True(messages.Count >= 2);
        var restored = new SessionLedger();
        restored.Restore("session-x", title, messages);
        Assert.Equal(ledger.Count("session-x"), restored.Count("session-x"));
    }

    [Fact]
    public void HistoryFileRollbackRestoresBackup()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-history-" + Guid.NewGuid().ToString("N")
        );
        var ledger = new SessionLedger();
        ledger.EnsureSession("session-y", "demo");
        ledger.Append("session-y", "user", "first", "turn-one");
        JsonlHistoryFile.Save(directory, "session-y", "demo", ledger.Snapshot("session-y"));
        ledger.Append("session-y", "user", "second", "turn-two");
        JsonlHistoryFile.Save(directory, "session-y", "demo", ledger.Snapshot("session-y"));
        Assert.True(JsonlHistoryFile.TryRollback(directory, "session-y"));
        Assert.True(JsonlHistoryFile.TryLoad(directory, "session-y", out _, out var messages));
        Assert.Single(messages);
    }

    [Fact]
    public async Task BadFrameSkippedAndLoopContinues()
    {
        await using var server = new FrameServer();
        var bad = await server.SubmitAsync(RequestFrame.WithPayload(210, string.Empty));
        Assert.False(bad.Ok);
        var good = await server.SubmitAsync(RequestFrame.WithPayload(211, "system.ping"));
        Assert.True(good.Ok);
    }

    [Fact]
    public async Task HostOnlyMethodReportsNotFound()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(
            RequestFrame.WithPayload(220, "host.pickDirectory")
        );
        Assert.False(response.Ok);
        Assert.Equal("NOT_FOUND", response.Error!.Code);
    }

    [Fact]
    public async Task CancelUnknownTurnReportsNotFound()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(
            RequestFrame.WithPayload(221, "agent.cancel", new { turnId = "missing" })
        );
        Assert.False(response.Ok);
        Assert.Equal("NOT_FOUND", response.Error!.Code);
    }
}
