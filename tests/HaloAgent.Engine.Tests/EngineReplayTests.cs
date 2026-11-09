using System.Diagnostics;
using System.Runtime.CompilerServices;
using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// True loop coverage: multi-slice streaming, retry echo, single-session
/// reentry, failure mapping, budget guard, wall-clock behavior and cancel.
/// The scripted gateway stands in for the provider pipeline so the loop can
/// be exercised without network access; the host shell reuses the same
/// recording shape when forwarding to the real pipeline.
/// </summary>
public sealed class EngineReplayTests
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

        public List<EngineLlmRequest> Seen { get; } = new();

        public async IAsyncEnumerable<EngineLlmEvent> StreamAsync(
            EngineLlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            lock (Seen)
            {
                Seen.Add(request);
            }
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

    private sealed class AllowBudget : IEngineBudget
    {
        public BudgetDecision Check(string sessionId, int estimatedTokens) => new(true, null);
    }

    private static async IAsyncEnumerable<EngineLlmEvent> StreamOf(
        IEnumerable<EngineLlmEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<EngineLlmEvent> StreamSlowText(
        string text,
        int delayMs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        yield return EngineLlmEvent.Text(text);
    }

    private static async IAsyncEnumerable<EngineLlmEvent> StreamHanging(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static async Task<string> NewSessionAsync(FrameServer server, long id = 1)
    {
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(id, "session.new", new { title = "loop" })
        );
        Assert.True(created.Ok);
        return created.Result!.Value.GetProperty("sessionId").GetString()!;
    }

    private static string PayloadText(EventFrame frame, string name)
    {
        if (
            frame.Payload.ValueKind == System.Text.Json.JsonValueKind.Object
            && frame.Payload.TryGetProperty(name, out var node)
            && node.ValueKind == System.Text.Json.JsonValueKind.String
        )
        {
            return node.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static long PayloadLength(EventFrame frame)
    {
        if (
            frame.Payload.ValueKind == System.Text.Json.JsonValueKind.Object
            && frame.Payload.TryGetProperty("length", out var node)
            && node.ValueKind == System.Text.Json.JsonValueKind.Number
            && node.TryGetInt64(out long value)
        )
        {
            return value;
        }
        return -1;
    }

    private static async Task<EventBatch> WaitForAsync(
        FrameServer server,
        Func<EventBatch, bool> ready,
        int timeoutMs = 8000
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            var batch = server.ReadEvents(0, 500);
            if (ready(batch))
            {
                return batch;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for the expected event batch.");
    }

    private static bool HasKind(EventBatch batch, string turnId, string kind) =>
        batch.Frames.Any(f =>
            string.Equals(f.Kind, kind, StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
        );

    private static EventFrame? FindKind(EventBatch batch, string turnId, string kind) =>
        batch.Frames.FirstOrDefault(f =>
            string.Equals(f.Kind, kind, StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
        );

    [Fact]
    public async Task TrueLoopEmitsMultipleSlicesAndDone()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[]
                    {
                        EngineLlmEvent.Text("Hello, "),
                        EngineLlmEvent.Text("this is "),
                        EngineLlmEvent.Text("a live turn."),
                        EngineLlmEvent.UsageUpdate(new EngineUsage(12, 9, 0)),
                    }
                )
        );
        server.ConfigureGateway(gateway);
        server.ConfigureBudget(new AllowBudget());

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(2, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(
            server,
            b => HasKind(b, turnId, "turn.done") || HasKind(b, turnId, "turn.failed")
        );
        Assert.True(HasKind(batch, turnId, "turn.done"));
        int slices = batch.Frames.Count(f =>
            string.Equals(f.Kind, "text.delta", StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
        );
        Assert.True(slices >= 3);
        var done = FindKind(batch, turnId, "text.done");
        Assert.NotNull(done);
        Assert.Equal("Hello, this is a live turn.".Length, PayloadLength(done!));
        Assert.True(HasKind(batch, turnId, "usage.tick"));

        var page = await server.SubmitAsync(
            RequestFrame.WithPayload(3, "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        var items = page.Result!.Value.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 2);
    }

    [Fact]
    public async Task SameCallerKeyReplaysSameTurn()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamOf(new[] { EngineLlmEvent.Text("slow body") })
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(
                11,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "first",
                    requestId = "dup-key-1"
                }
            )
        );
        Assert.True(first.Ok);
        string firstTurn = first.Result!.Value.GetProperty("turnId").GetString()!;

        var second = await server.SubmitAsync(
            RequestFrame.WithPayload(
                12,
                "agent.prompt",
                new
                {
                    sessionId,
                    text = "first",
                    requestId = "dup-key-1"
                }
            )
        );
        Assert.True(second.Ok);
        string secondTurn = second.Result!.Value.GetProperty("turnId").GetString()!;
        Assert.Equal(firstTurn, secondTurn);

        var batch = await WaitForAsync(server, b => HasKind(b, firstTurn, "turn.done"));
        Assert.True(HasKind(batch, firstTurn, "turn.done"));
        int started = batch.Frames.Count(f =>
            string.Equals(f.Kind, "turn.started", StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), firstTurn, StringComparison.Ordinal)
        );
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task SecondPromptWhileActiveReportsInProgress()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamSlowText("late body", 1500, token)
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(21, "agent.prompt", new { sessionId, text = "first" })
        );
        Assert.True(first.Ok);

        var second = await server.SubmitAsync(
            RequestFrame.WithPayload(22, "agent.prompt", new { sessionId, text = "second" })
        );
        Assert.False(second.Ok);
        Assert.Equal("TURN_IN_PROGRESS", second.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(second.Error.Hint));
    }

    [Fact]
    public async Task ProviderAuthFailureMapsToFrozenReason()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[] { EngineLlmEvent.Failure("HTTP_401", "unauthorized: bad key", 401), }
                )
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(31, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.failed"));
        var failed = FindKind(batch, turnId, "turn.failed");
        Assert.NotNull(failed);
        Assert.Equal("PROVIDER_AUTH", PayloadText(failed!, "reason"));
    }

    [Fact]
    public async Task TransportFailureMapsToNetworkFail()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[] { EngineLlmEvent.Failure("NETWORK_FAIL", "connection reset by peer"), }
                )
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(41, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.failed"));
        var failed = FindKind(batch, turnId, "turn.failed");
        Assert.NotNull(failed);
        Assert.Equal("NETWORK_FAIL", PayloadText(failed!, "reason"));
    }

    [Fact]
    public async Task SingleCallTimeoutMapsToToolTimeout()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[]
                    {
                        EngineLlmEvent.Failure("TIMEOUT", "single call exceeded its slice budget"),
                    }
                )
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(51, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.failed"));
        var failed = FindKind(batch, turnId, "turn.failed");
        Assert.NotNull(failed);
        Assert.Equal("TOOL_TIMEOUT", PayloadText(failed!, "reason"));
    }

    [Fact]
    public async Task BackpressureMapsToRateLimited()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[] { EngineLlmEvent.Failure("RATE_LIMITED", "backpressure: slow down"), }
                )
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(61, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.failed"));
        var failed = FindKind(batch, turnId, "turn.failed");
        Assert.NotNull(failed);
        Assert.Equal("RATE_LIMITED", PayloadText(failed!, "reason"));
    }

    [Fact]
    public async Task BudgetExceededStopsBeforeGateway()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamOf(new[] { EngineLlmEvent.Text("must not run") })
        );
        server.ConfigureGateway(gateway);
        server.ConfigureBudget(new DenyBudget());

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(71, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.failed"));
        var failed = FindKind(batch, turnId, "turn.failed");
        Assert.NotNull(failed);
        Assert.Equal("RATE_LIMITED", PayloadText(failed!, "reason"));
        Assert.Empty(gateway.Seen);
    }

    [Fact]
    public async Task SlowTurnCompletesWithoutWholeTurnCap()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) => StreamSlowText("patient body", 600, token)
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(81, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var batch = await WaitForAsync(
            server,
            b => HasKind(b, turnId, "turn.done") || HasKind(b, turnId, "turn.failed"),
            timeoutMs: 10000
        );
        Assert.True(HasKind(batch, turnId, "turn.done"));
        Assert.False(HasKind(batch, turnId, "turn.failed"));
    }

    [Fact]
    public async Task HistoryReadsStayFastBesideARunningTurn()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway(
            (request, token) =>
                StreamOf(
                    new[]
                    {
                        EngineLlmEvent.Text("alpha "),
                        EngineLlmEvent.Text("beta "),
                        EngineLlmEvent.Text("gamma"),
                    }
                )
        );
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(91, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var latencies = new List<long>();
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var page = await server.SubmitAsync(
                RequestFrame.WithPayload(100 + i, "session.history", new { sessionId, limit = 50 })
            );
            watch.Stop();
            Assert.True(page.Ok);
            latencies.Add(watch.ElapsedMilliseconds);
        }
        latencies.Sort();
        long privileged = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1];
        Assert.True(privileged < 200);

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.done"));
        Assert.True(HasKind(batch, turnId, "turn.done"));
    }

    [Fact]
    public async Task CancelStaysOnTheFastestPath()
    {
        await using var server = new FrameServer();
        var gateway = new ScriptGateway((request, token) => StreamHanging(token));
        server.ConfigureGateway(gateway);

        string sessionId = await NewSessionAsync(server);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(130, "agent.prompt", new { sessionId, text = "hi" })
        );
        Assert.True(answered.Ok);
        string turnId = answered.Result!.Value.GetProperty("turnId").GetString()!;

        var watch = Stopwatch.StartNew();
        var cancelled = await server.SubmitAsync(
            RequestFrame.WithPayload(131, "agent.cancel", new { turnId })
        );
        watch.Stop();
        Assert.True(cancelled.Ok);
        Assert.True(watch.ElapsedMilliseconds < 2000);

        var batch = await WaitForAsync(server, b => HasKind(b, turnId, "turn.cancelled"));
        Assert.True(HasKind(batch, turnId, "turn.cancelled"));
    }

    [Fact]
    public void FailureMappingCoversEveryEmissionCode()
    {
        Assert.Equal("PROVIDER_AUTH", TurnRunner.MapFailure("HTTP_401", 401, "unauthorized").Code);
        Assert.Equal(
            "NETWORK_FAIL",
            TurnRunner.MapFailure("NETWORK_FAIL", null, "connection reset").Code
        );
        var limited = TurnRunner.MapFailure("RATE_LIMITED", 429, "backpressure: slow down");
        Assert.Equal("RATE_LIMITED", limited.Code);
        Assert.Equal(
            "TOOL_TIMEOUT",
            TurnRunner.MapFailure("TIMEOUT", null, "single call exceeded").Code
        );
    }

    [Fact]
    public void RepairHintsStayBusinessShaped()
    {
        Assert.Contains("Hint:", TurnRunner.HintForTool("read_file"));
        Assert.Contains("Hint:", TurnRunner.HintForTool("edit_file", "OLD_STRING_NOT_FOUND"));
        Assert.NotEmpty(TurnRunner.BuildNextActionHint("edit_lines", "{}", "MISSING_LIMIT"));
    }
}
