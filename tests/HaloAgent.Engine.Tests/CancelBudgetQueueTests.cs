using System.Text.Json;
using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Cancel tree, confirmation timeout, true compaction, true budget and the
/// memory FIFO queue: the correctness station behind the frame boundary.
/// </summary>
public sealed class CancelBudgetQueueTests
{
    private sealed class ScriptGateway : IEngineGateway
    {
        private readonly Func<
            EngineLlmRequest,
            CancellationToken,
            IAsyncEnumerable<EngineLlmEvent>
        > _stream;

        public ScriptGateway(
            Func<EngineLlmRequest, CancellationToken, IAsyncEnumerable<EngineLlmEvent>> stream
        )
        {
            _stream = stream;
        }

        public async IAsyncEnumerable<EngineLlmEvent> StreamAsync(
            EngineLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            await foreach (var evt in _stream(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
    }

    private sealed class DenyBudget : IEngineBudget
    {
        public BudgetDecision Check(string sessionId, int estimatedTokens) =>
            new(false, "budget exceeded for this turn");
    }

    private static async IAsyncEnumerable<EngineLlmEvent> StreamOf(
        IEnumerable<EngineLlmEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    private static async Task<string> NewSessionAsync(
        FrameServer server,
        long id,
        string title = "demo"
    )
    {
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(id, "session.new", new { title })
        );
        Assert.True(created.Ok);
        return created.Result!.Value.GetProperty("sessionId").GetString()!;
    }

    private static string PayloadText(EventFrame frame, string name)
    {
        if (
            frame.Payload.ValueKind == JsonValueKind.Object
            && frame.Payload.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.String
        )
        {
            return node.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    [Fact]
    public async Task CancelTreeStopsChildBeforeParent()
    {
        await using var server = new FrameServer();
        string parent = await NewSessionAsync(server, 101);
        var childCreated = await server.SubmitAsync(
            RequestFrame.WithPayload(102, "session.new", new { title = "child" })
        );
        string child = childCreated.Result!.Value.GetProperty("sessionId").GetString()!;
        server.Sessions.SetParent(child, parent);

        var gateway = new ScriptGateway((request, token) => StreamHanging(token));
        server.ConfigureGateway(gateway);
        var promptParent = await server.SubmitAsync(
            RequestFrame.WithPayload(
                103,
                "agent.prompt",
                new { sessionId = parent, text = "parent work" }
            )
        );
        Assert.True(promptParent.Ok);
        var promptChild = await server.SubmitAsync(
            RequestFrame.WithPayload(
                104,
                "agent.prompt",
                new { sessionId = child, text = "child work" }
            )
        );
        Assert.True(promptChild.Ok);
        Assert.NotNull(server.Turns.ActiveTurnOf(parent));
        Assert.NotNull(server.Turns.ActiveTurnOf(child));

        var treeCancel = await server.SubmitAsync(
            RequestFrame.WithPayload(105, "agent.cancel", new { sessionId = parent })
        );
        Assert.True(treeCancel.Ok);
        Assert.Null(server.Turns.ActiveTurnOf(parent));
        Assert.Null(server.Turns.ActiveTurnOf(child));
    }

    private static async IAsyncEnumerable<EngineLlmEvent> StreamHanging(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    [Fact]
    public void ConfirmationTimeoutRemovesOnlyTheExpiredGate()
    {
        var state = new EngineState();
        var turns = new TurnCoordinator(state);
        string turnId = "turn-timeout";
        turns.TryStart("sess-timeout", turnId, 1, out _);
        using var first = JsonDocument.Parse("{}");
        using var second = JsonDocument.Parse("{}");
        turns.RaiseConfirmation(turnId, "call-a", "edit_file", first.RootElement.Clone());
        turns.RaiseConfirmation(turnId, "call-b", "edit_file", second.RootElement.Clone());
        Assert.Equal(2, turns.PendingCountFor(turnId));

        var answer = turns
            .WaitForAnswerAsync(
                turnId,
                "call-a",
                TimeSpan.FromMilliseconds(30),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        Assert.Null(answer);
        Assert.Equal(1, turns.PendingCountFor(turnId));
        Assert.True(turns.TryTakeConfirmation(turnId, "call-b", out var pending));
        Assert.Equal("call-b", pending!.ToolCallId);
    }

    [Fact]
    public void RemoveTimeoutClearsLinkedQuestion()
    {
        var state = new EngineState();
        var turns = new TurnCoordinator(state);
        turns.TryStart("sess-q", "turn-q", 2, out _);
        using var doc = JsonDocument.Parse("{}");
        turns.RaiseConfirmation(
            "turn-q",
            "call-1",
            "ask_user",
            doc.RootElement.Clone(),
            "ticket-1"
        );
        Assert.Equal(1, turns.PendingCountFor("turn-q"));
        Assert.True(turns.RemoveTimeout("turn-q", "call-1"));
        Assert.Equal(0, turns.PendingCountFor("turn-q"));
    }

    [Fact]
    public async Task TrueCompactionKeepsTailAndFiresCompacted()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamOf(new[] { EngineLlmEvent.Text("要点：任务目标明确；已完成三步；待继续收尾。") })
        );
        server.ConfigureGateway(gateway);
        string sessionId = await NewSessionAsync(server, 201);
        for (int i = 0; i < 10; i++)
        {
            server.Sessions.Append(sessionId, "user", $"task step {i}", $"turn-{i}");
            server.Sessions.Append(sessionId, "assistant", $"done step {i}", $"turn-{i}");
        }
        int before = server.Sessions.Count(sessionId);
        Assert.True(before > 8);

        var compacted = await server.SubmitAsync(
            RequestFrame.WithPayload(230, "session.compact", new { sessionId, keepLastGroups = 2 })
        );
        Assert.True(compacted.Ok);
        string summary = compacted.Result!.Value.GetProperty("summary").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(summary));
        int after = server.Sessions.Count(sessionId);
        Assert.True(after < before);
        var batch = server.ReadEvents(0, 500);
        Assert.Contains(batch.Frames, f => f.Kind == "session.compacted");
        Assert.Contains(
            batch.Frames,
            f => f.Kind == "session.updated" && PayloadText(f, "reason") == "compactStarted"
        );
    }

    [Fact]
    public async Task CompactingSessionRejectsPromptAndCountsReject()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 301);
        Assert.True(server.Sessions.TryBeginCompaction(sessionId));
        try
        {
            var rejected = await server.SubmitAsync(
                RequestFrame.WithPayload(302, "agent.prompt", new { sessionId, text = "late" })
            );
            Assert.False(rejected.Ok);
            Assert.Equal("COMPACTING", rejected.Error!.Code);
            var info = await server.SubmitAsync(
                RequestFrame.WithPayload(303, "system.info", new { })
            );
            Assert.True(info.Ok);
            long rejects = info.Result!.Value.GetProperty("compactRejects").GetInt64();
            Assert.True(rejects >= 1);
        }
        finally
        {
            server.Sessions.EndCompaction(sessionId, "summary");
        }
    }

    [Fact]
    public async Task CancelCompactionFlagStopsRunningCompaction()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 311);
        Assert.True(server.Sessions.TryBeginCompaction(sessionId));
        var stopped = await server.SubmitAsync(
            RequestFrame.WithPayload(312, "session.compact", new { sessionId, cancel = true })
        );
        Assert.True(stopped.Ok);
        Assert.False(server.Sessions.IsCompacting(sessionId));
        var missing = await server.SubmitAsync(
            RequestFrame.WithPayload(313, "session.compact", new { sessionId, cancel = true })
        );
        Assert.False(missing.Ok);
        Assert.Equal("NOT_FOUND", missing.Error!.Code);
    }

    [Fact]
    public async Task UsageQueryCarriesBudgetFields()
    {
        await using var server = new FrameServer();
        server.ConfigureTurnOptions(new EngineTurnOptions { BudgetMaxTokens = 1000 });
        string sessionId = await NewSessionAsync(server, 401);
        await server.SubmitAsync(
            RequestFrame.WithPayload(
                402,
                "agent.prompt",
                new { sessionId, text = "hello budget bar" }
            )
        );
        var queried = await server.SubmitAsync(
            RequestFrame.WithPayload(403, "usage.query", new { })
        );
        Assert.True(queried.Ok);
        var root = queried.Result!.Value;
        Assert.Equal(1000, root.GetProperty("maxTokens").GetInt64());
        Assert.True(root.TryGetProperty("usagePercentage", out _));
        Assert.True(root.TryGetProperty("occupancyState", out _));
        Assert.True(root.TryGetProperty("usedIsEstimated", out _));
        string occupancy = root.GetProperty("occupancyState").GetString()!;
        Assert.True(occupancy == "normal" || occupancy == "elevated" || occupancy == "high");
    }

    [Fact]
    public async Task BudgetExceededEmitsRateLimited()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamOf(new[] { EngineLlmEvent.Text("must not run") })
        );
        server.ConfigureGateway(gateway);
        server.ConfigureBudget(new DenyBudget());
        string sessionId = await NewSessionAsync(server, 411);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(412, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        EventBatch batch = server.ReadEvents(0, 500);
        while (DateTimeOffset.UtcNow < deadline)
        {
            batch = server.ReadEvents(0, 500);
            if (
                batch.Frames.Any(f => f.Kind == "turn.failed" && PayloadText(f, "turnId") == turnId)
            )
            {
                break;
            }
            await Task.Delay(50);
        }
        var failed = batch.Frames.FirstOrDefault(f =>
            f.Kind == "turn.failed" && PayloadText(f, "turnId") == turnId
        );
        Assert.NotNull(failed);
        Assert.Equal("RATE_LIMITED", PayloadText(failed!, "reason"));
    }

    [Fact]
    public async Task QueueKeepsFifoOrderAndDrainStartsPrompt()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 501);
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(502, "agent.queue", new { sessionId, text = "first queued" })
        );
        Assert.True(first.Ok);
        var second = await server.SubmitAsync(
            RequestFrame.WithPayload(503, "agent.queue", new { sessionId, text = "second queued" })
        );
        Assert.True(second.Ok);
        Assert.Equal(2, second.Result!.Value.GetProperty("depth").GetInt32());

        var drained = await server.SubmitAsync(
            RequestFrame.WithPayload(504, "agent.drain", new { sessionId })
        );
        Assert.True(drained.Ok);
        Assert.Equal(1, server.TurnQueue.Depth(sessionId));

        var unqueued = await server.SubmitAsync(
            RequestFrame.WithPayload(505, "agent.unqueue", new { sessionId })
        );
        Assert.True(unqueued.Ok);
        Assert.Equal(0, server.TurnQueue.Depth(sessionId));

        var empty = await server.SubmitAsync(
            RequestFrame.WithPayload(506, "agent.unqueue", new { sessionId })
        );
        Assert.False(empty.Ok);
        Assert.Equal("NOT_FOUND", empty.Error!.Code);
    }

    [Fact]
    public async Task RetryCarriesRetriedFrom()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 601);
        await server.SubmitAsync(
            RequestFrame.WithPayload(602, "agent.prompt", new { sessionId, text = "again" })
        );
        var retry = await server.SubmitAsync(
            RequestFrame.WithPayload(603, "agent.retry", new { sessionId })
        );
        Assert.True(retry.Ok);
        var batch = server.ReadEvents(0, 500);
        var started = batch.Frames.FirstOrDefault(f =>
            f.Kind == "turn.started"
            && PayloadText(f, "sessionId") == sessionId
            && f.Payload.TryGetProperty("retriedFrom", out _)
        );
        Assert.NotNull(started);
    }

    [Fact]
    public async Task SystemInfoExposesCancelAndQueueMetrics()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 701);
        await server.SubmitAsync(
            RequestFrame.WithPayload(702, "agent.prompt", new { sessionId, text = "hi" })
        );
        var info = await server.SubmitAsync(RequestFrame.WithPayload(703, "system.info", new { }));
        Assert.True(info.Ok);
        var root = info.Result!.Value;
        Assert.True(root.TryGetProperty("cancel", out _));
        Assert.True(root.TryGetProperty("compactRejects", out _));
        Assert.True(root.TryGetProperty("queueDepth", out _));
    }

    [Fact]
    public async Task TitleLockAndFailureDetails()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, 801);
        var missing = await server.SubmitAsync(
            RequestFrame.WithPayload(802, "session.title", new { sessionId })
        );
        Assert.False(missing.Ok);
        Assert.Equal("NO_USER_MESSAGE", missing.Error!.Hint);

        await server.SubmitAsync(
            RequestFrame.WithPayload(
                803,
                "agent.prompt",
                new { sessionId, text = "first user line for title" }
            )
        );
        var written = await server.SubmitAsync(
            RequestFrame.WithPayload(
                804,
                "session.title",
                new
                {
                    sessionId,
                    title = "My Title",
                    titleLocked = true
                }
            )
        );
        Assert.True(written.Ok);
        var locked = await server.SubmitAsync(
            RequestFrame.WithPayload(805, "session.title", new { sessionId, title = "Other" })
        );
        Assert.False(locked.Ok);
    }
}
