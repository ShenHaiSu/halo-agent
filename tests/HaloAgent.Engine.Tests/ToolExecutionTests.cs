using System.Diagnostics;
using System.Runtime.CompilerServices;
using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// True tool station coverage: fifteen-tool dispatch, unknown-tool isolation,
/// safety mapping, spill shaping, timeout isolation, write approval, ask_user
/// relay and keyed answers. A scripted gateway drives the planner loop while a
/// fake backend stands in for the host pipeline; the shell wires the real
/// registry behind the same seam.
/// </summary>
public sealed class ToolExecutionTests
{
    private sealed class FakeBackend : IEngineToolBackend
    {
        public Func<
            string,
            string,
            EngineToolBackendContext,
            CancellationToken,
            Task<EngineBackendToolResult>
        >? Handler;

        public List<(string Tool, string Args)> Seen { get; } = new();

        public EngineToolBackendContext? LastContext { get; private set; }

        public Task<EngineBackendToolResult> InvokeAsync(
            string toolName,
            string argumentsJson,
            EngineToolBackendContext context,
            CancellationToken cancellationToken
        )
        {
            lock (Seen)
            {
                Seen.Add((toolName, argumentsJson));
            }
            LastContext = context;
            if (Handler is null)
            {
                return Task.FromResult(new EngineBackendToolResult(true, "ok", null, 0, 1));
            }
            return Handler(toolName, argumentsJson, context, cancellationToken);
        }
    }

    private sealed class PlannedGateway : IEngineGateway
    {
        private readonly Queue<IReadOnlyList<EngineLlmEvent>> _plans = new();

        public void Enqueue(params EngineLlmEvent[] events)
        {
            lock (_plans)
            {
                _plans.Enqueue(events);
            }
        }

        public async IAsyncEnumerable<EngineLlmEvent> StreamAsync(
            EngineLlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            IReadOnlyList<EngineLlmEvent> plan;
            lock (_plans)
            {
                plan = _plans.Count > 0 ? _plans.Dequeue() : new[] { EngineLlmEvent.Text("done") };
            }
            foreach (EngineLlmEvent evt in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
                await Task.Yield();
            }
        }
    }

    private static readonly string[] FifteenTools =
    {
        "read_file",
        "list_dir",
        "grep_search",
        "view_image",
        "exec_command",
        "edit_file",
        "edit_lines",
        "edit",
        "write_file",
        "undo_edit",
        "ask_user",
        "skill_tool",
        "memory_tool",
        "spawn_subagent",
        "goal_tool",
    };

    private static async Task<string> NewSessionAsync(FrameServer server, long id = 1)
    {
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(id, "session.new", new { title = "tools" })
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

    private static bool PayloadBool(EventFrame frame, string name, bool fallback = false)
    {
        if (
            frame.Payload.ValueKind == System.Text.Json.JsonValueKind.Object
            && frame.Payload.TryGetProperty(name, out var node)
            && (
                node.ValueKind == System.Text.Json.JsonValueKind.True
                || node.ValueKind == System.Text.Json.JsonValueKind.False
            )
        )
        {
            return node.GetBoolean();
        }
        return fallback;
    }

    private static async Task<EventBatch> WaitForAsync(
        FrameServer server,
        Func<EventBatch, bool> ready,
        int timeoutMs = 10000
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            var batch = server.ReadEvents(0, 2000);
            if (ready(batch))
            {
                return batch;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for the expected event batch.");
    }

    private static EventFrame? FindToolEnd(EventBatch batch, string turnId, string toolCallId) =>
        batch.Frames.FirstOrDefault(f =>
            string.Equals(f.Kind, "tool.end", StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "toolCallId"), toolCallId, StringComparison.Ordinal)
        );

    private static EventFrame? FindToolConfirm(
        EventBatch batch,
        string turnId,
        string toolCallId
    ) =>
        batch.Frames.FirstOrDefault(f =>
            string.Equals(f.Kind, "tool.confirm", StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "toolCallId"), toolCallId, StringComparison.Ordinal)
        );

    private static string TempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "halo-engine-tools",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task FifteenToolsDispatchWithFullCardFields()
    {
        string root = TempRoot();
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
        {
            if (string.Equals(tool, "view_image", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new EngineBackendToolResult(
                        true,
                        "image bytes",
                        null,
                        0,
                        3,
                        Images: new[]
                        {
                            new EngineImageReference(
                                "shot.png",
                                "attachments/shot.png",
                                "image/png",
                                64,
                                64,
                                128
                            ),
                        }
                    )
                );
            }
            if (string.Equals(tool, "write_file", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new EngineBackendToolResult(true, "wrote 12 bytes", null, 0, 3, FileBytes: 12)
                );
            }
            if (string.Equals(tool, "spawn_subagent", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new EngineBackendToolResult(true, EngineToolExecutor.SubAgentMarker, null, 0, 3)
                );
            }
            if (string.Equals(tool, "edit_file", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    new EngineBackendToolResult(
                        false,
                        string.Empty,
                        "OLD_STRING_NOT_FOUND: anchor was not found.",
                        -1,
                        2,
                        "OLD_STRING_NOT_FOUND",
                        "{\"nearest\": 42}"
                    )
                );
            }
            return Task.FromResult(new EngineBackendToolResult(true, $"[{tool}] ok", null, 0, 2));
        };

        var executor = new EngineToolExecutor(
            backend,
            new EngineToolExecutor.Options { DataRoot = root }
        );
        var calls = FifteenTools
            .Where(t => t != "ask_user")
            .Select((t, i) => new EngineToolCall($"call_{i:00}", t, "{\"probe\": true}"))
            .ToList();

        IReadOnlyList<EngineToolResult> outcomes = await executor.ExecuteAsync(
            "sess_tools",
            "turn_tools",
            calls,
            CancellationToken.None
        );

        Assert.Equal(calls.Count, outcomes.Count);
        foreach (EngineToolResult outcome in outcomes)
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome.Output));
            Assert.True(outcome.DurationMs >= 0);
            Assert.NotNull(outcome.ExitCode);
        }

        EngineToolResult view = outcomes.Single(o => o.ToolName == "view_image");
        Assert.NotNull(view.Images);
        Assert.Single(view.Images!);
        Assert.Equal("shot.png", view.Images![0].FileName);

        EngineToolResult written = outcomes.Single(o => o.ToolName == "write_file");
        Assert.Equal(12, written.FileBytes);

        EngineToolResult spawned = outcomes.Single(o => o.ToolName == "spawn_subagent");
        Assert.Contains(EngineToolExecutor.SubAgentMarker, spawned.Output);

        EngineToolResult editFailed = outcomes.Single(o => o.ToolName == "edit_file");
        Assert.False(editFailed.Success);
        Assert.Equal("OLD_STRING_NOT_FOUND", editFailed.ErrorCode);
        Assert.Contains("nearest", editFailed.ErrorPayloadJson ?? string.Empty);
        Assert.False(
            string.IsNullOrWhiteSpace(TurnRunner.HintForTool("edit_file", "OLD_STRING_NOT_FOUND"))
        );

        Assert.Equal("main", backend.LastContext!.AgentRole);
    }

    [Fact]
    public async Task UnknownToolStaysInsideToolDenied()
    {
        string root = TempRoot();
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
            Task.FromResult(
                new EngineBackendToolResult(
                    false,
                    string.Empty,
                    $"UNKNOWN_TOOL: unknown tool '{tool}'.",
                    -1,
                    1,
                    "UNKNOWN_TOOL"
                )
            );
        var executor = new EngineToolExecutor(
            backend,
            new EngineToolExecutor.Options { DataRoot = root }
        );

        IReadOnlyList<EngineToolResult> outcomes = await executor.ExecuteAsync(
            "sess_unknown",
            "turn_unknown",
            new[] { new EngineToolCall("call_nope", "no_such_tool", "{}") },
            CancellationToken.None
        );

        EngineToolResult only = Assert.Single(outcomes);
        Assert.False(only.Success);
        Assert.Equal("UNKNOWN_TOOL", only.ErrorCode);
        Assert.Contains("TOOL_DENIED", only.ErrorPayloadJson ?? string.Empty);

        // The loop surfaces the miss as a tool card, never as a cutover NOT_FOUND.
        await using var server = new FrameServer();
        var gateway = new PlannedGateway();
        gateway.Enqueue(EngineLlmEvent.Tool(new EngineToolCall("call_nope", "no_such_tool", "{}")));
        gateway.Enqueue(EngineLlmEvent.Text("recovered"));
        server.ConfigureGateway(gateway);
        server.ConfigureToolRunner(
            new EngineToolExecutor(backend, new EngineToolExecutor.Options { DataRoot = root })
        );
        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(11, "agent.prompt", new { sessionId, text = "run it" })
        );
        Assert.True(prompted.Ok);
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;
        var batch = await WaitForAsync(
            server,
            b => FindToolEnd(b, turnId, "call_nope") is not null
        );
        EventFrame end = FindToolEnd(batch, turnId, "call_nope")!;
        Assert.False(PayloadBool(end, "success", true));
        Assert.Equal("UNKNOWN_TOOL", PayloadText(end, "errorCode"));
        Assert.DoesNotContain(
            batch.Frames,
            f => string.Equals(f.Kind, "turn.failed", StringComparison.Ordinal)
        );
        await WaitForAsync(
            server,
            b =>
                b.Frames.Any(f =>
                    string.Equals(f.Kind, "turn.done", StringComparison.Ordinal)
                    && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
                )
        );
    }

    [Fact]
    public async Task SafetyViolationsShareOneDeniedMapping()
    {
        string root = TempRoot();
        foreach (string code in new[] { "PATH_DENIED", "PATH_OUTSIDE_WORKSPACE" })
        {
            var backend = new FakeBackend();
            backend.Handler = (tool, args, ctx, ct) =>
                Task.FromResult(
                    new EngineBackendToolResult(
                        false,
                        string.Empty,
                        $"{code}: outside.",
                        -1,
                        1,
                        code
                    )
                );
            var executor = new EngineToolExecutor(
                backend,
                new EngineToolExecutor.Options { DataRoot = root }
            );
            IReadOnlyList<EngineToolResult> outcomes = await executor.ExecuteAsync(
                "sess_safe",
                "turn_safe",
                new[]
                {
                    new EngineToolCall("call_path", "read_file", "{\"file_path\": \"/etc/passwd\"}")
                },
                CancellationToken.None
            );
            EngineToolResult only = Assert.Single(outcomes);
            Assert.Equal(code, only.ErrorCode);
            Assert.Contains("TOOL_DENIED", only.ErrorPayloadJson ?? string.Empty);
        }
    }

    [Fact]
    public async Task OversizedOutputSpillsWithObservableTruncation()
    {
        string root = TempRoot();
        string huge = new string('x', 70000);
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
            Task.FromResult(new EngineBackendToolResult(true, huge, null, 0, 5));
        var executor = new EngineToolExecutor(
            backend,
            new EngineToolExecutor.Options { DataRoot = root }
        );

        IReadOnlyList<EngineToolResult> outcomes = await executor.ExecuteAsync(
            "sess_spill",
            "turn_spill",
            new[] { new EngineToolCall("call_big", "read_file", "{}") },
            CancellationToken.None
        );

        EngineToolResult only = Assert.Single(outcomes);
        Assert.True(only.Success);
        Assert.True(only.Truncated);
        Assert.Equal(70000 - EngineToolPolicy.DisplayLimit, only.TruncatedChars);
        Assert.False(string.IsNullOrWhiteSpace(only.SpilledFile));
        Assert.StartsWith("sessions/", only.SpilledFile);
        Assert.True(
            File.Exists(
                Path.Combine(root, only.SpilledFile!.Replace('/', Path.DirectorySeparatorChar))
            )
        );
        Assert.True(only.Output.Length <= EngineToolPolicy.DisplayLimit + 256);
        Assert.NotNull(only.PersistedText);
        Assert.True(only.PersistedText!.Length <= EngineToolPolicy.PersistLimit + 256);
    }

    [Fact]
    public async Task ExecTimeoutBoundariesAndIsolation()
    {
        string root = TempRoot();
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
            Task.FromResult(new EngineBackendToolResult(true, "fast", null, 0, 1));
        var executor = new EngineToolExecutor(
            backend,
            new EngineToolExecutor.Options { DataRoot = root }
        );

        foreach (int good in new[] { 1, 300000 })
        {
            IReadOnlyList<EngineToolResult> ok = await executor.ExecuteAsync(
                "sess_exec",
                "turn_exec",
                new[]
                {
                    new EngineToolCall("call_exec", "exec_command", $"{{\"timeout_ms\": {good}}}")
                },
                CancellationToken.None
            );
            Assert.True(Assert.Single(ok).Success);
        }

        foreach (int bad in new[] { 0, 300001 })
        {
            IReadOnlyList<EngineToolResult> denied = await executor.ExecuteAsync(
                "sess_exec",
                "turn_exec",
                new[]
                {
                    new EngineToolCall("call_exec", "exec_command", $"{{\"timeout_ms\": {bad}}}")
                },
                CancellationToken.None
            );
            EngineToolResult only = Assert.Single(denied);
            Assert.False(only.Success);
            Assert.Equal("INVALID_TIMEOUT", only.ErrorCode);
        }

        var slow = new FakeBackend();
        slow.Handler = async (tool, args, ctx, ct) =>
        {
            await Task.Delay(3000, ct).ConfigureAwait(false);
            return new EngineBackendToolResult(true, "slow", null, 0, 3000);
        };
        var guarded = new EngineToolExecutor(
            slow,
            new EngineToolExecutor.Options { DataRoot = root }
        );
        IReadOnlyList<EngineToolResult> timed = await guarded.ExecuteAsync(
            "sess_exec",
            "turn_exec",
            new[] { new EngineToolCall("call_slow", "exec_command", "{\"timeout_ms\": 50}") },
            CancellationToken.None
        );
        EngineToolResult timeout = Assert.Single(timed);
        Assert.False(timeout.Success);
        Assert.Equal("TOOL_TIMEOUT", timeout.ErrorCode);
        Assert.Equal(50, timeout.TimeoutMs);
    }

    [Fact]
    public async Task WriteToolsPauseForApprovalAndResume()
    {
        await using var server = new FrameServer();
        var gateway = new PlannedGateway();
        gateway.Enqueue(
            EngineLlmEvent.Tool(
                new EngineToolCall("call_write", "write_file", "{\"file_path\": \"a.txt\"}")
            )
        );
        gateway.Enqueue(EngineLlmEvent.Text("saved"));
        server.ConfigureGateway(gateway);
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
            Task.FromResult(
                new EngineBackendToolResult(true, "wrote a.txt", null, 0, 2, FileBytes: 9)
            );
        server.ConfigureToolRunner(
            new EngineToolExecutor(
                backend,
                new EngineToolExecutor.Options { DataRoot = TempRoot() }
            )
        );

        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(21, "agent.prompt", new { sessionId, text = "write it" })
        );
        Assert.True(prompted.Ok);
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;

        var confirmed = await WaitForAsync(
            server,
            b => FindToolConfirm(b, turnId, "call_write") is not null
        );
        EventFrame confirm = FindToolConfirm(confirmed, turnId, "call_write")!;
        Assert.Equal("confirm", PayloadText(confirm, "policy"));
        Assert.Contains("a.txt", PayloadText(confirm, "argsPreview"));

        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(
                22,
                "agent.answer",
                new
                {
                    turnId,
                    toolCallId = "call_write",
                    approved = true
                }
            )
        );
        Assert.True(answered.Ok);

        var batch = await WaitForAsync(
            server,
            b => FindToolEnd(b, turnId, "call_write") is not null
        );
        EventFrame end = FindToolEnd(batch, turnId, "call_write")!;
        Assert.True(PayloadBool(end, "success"));
        Assert.Contains("wrote", PayloadText(end, "output"));
        await WaitForAsync(
            server,
            b =>
                b.Frames.Any(f =>
                    string.Equals(f.Kind, "turn.done", StringComparison.Ordinal)
                    && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
                )
        );

        StoredMessage history = server
            .Sessions.Snapshot(sessionId)
            .First(m => string.Equals(m.ToolCallId, "call_write", StringComparison.Ordinal));
        Assert.Equal("tool", history.Role);
        Assert.Equal("write_file", history.Name);
        Assert.Contains("wrote", history.Text);
    }

    [Fact]
    public async Task RejectedWriteEndsTheChainAsRejected()
    {
        await using var server = new FrameServer();
        var gateway = new PlannedGateway();
        gateway.Enqueue(
            EngineLlmEvent.Tool(
                new EngineToolCall("call_exec", "exec_command", "{\"command\": \"rm -rf /\"}")
            )
        );
        server.ConfigureGateway(gateway);
        var backend = new FakeBackend();
        server.ConfigureToolRunner(
            new EngineToolExecutor(
                backend,
                new EngineToolExecutor.Options { DataRoot = TempRoot() }
            )
        );

        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(31, "agent.prompt", new { sessionId, text = "wipe it" })
        );
        Assert.True(prompted.Ok);
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;

        await WaitForAsync(server, b => FindToolConfirm(b, turnId, "call_exec") is not null);
        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(
                32,
                "agent.answer",
                new
                {
                    turnId,
                    toolCallId = "call_exec",
                    approved = false
                }
            )
        );
        Assert.True(answered.Ok);

        var batch = await WaitForAsync(
            server,
            b =>
                b.Frames.Any(f =>
                    string.Equals(f.Kind, "turn.cancelled", StringComparison.Ordinal)
                    && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
                )
        );
        EventFrame cancelled = batch.Frames.First(f =>
            string.Equals(f.Kind, "turn.cancelled", StringComparison.Ordinal)
            && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
        );
        Assert.Equal("rejected", PayloadText(cancelled, "reason"));

        EventFrame? denied = FindToolEnd(
            await WaitForAsync(server, b => FindToolEnd(b, turnId, "call_exec") is not null),
            turnId,
            "call_exec"
        );
        Assert.NotNull(denied);
        Assert.False(PayloadBool(denied!, "success", true));
        Assert.Equal("TOOL_DENIED", PayloadText(denied!, "errorCode"));
        Assert.Empty(backend.Seen);
    }

    [Fact]
    public async Task AnswersVerifyThePendingCallKey()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        using var args = System.Text.Json.JsonDocument.Parse("{\"file_path\": \"a.txt\"}");

        server.Turns.TryStart(sessionId, "turn_keys", 900, out _);
        server.Turns.RaiseConfirmation(
            "turn_keys",
            "call_a",
            "write_file",
            args.RootElement.Clone()
        );
        server.Turns.RaiseConfirmation(
            "turn_keys",
            "call_b",
            "exec_command",
            args.RootElement.Clone()
        );
        Assert.Equal(2, server.Turns.PendingCountFor("turn_keys"));

        var ambiguous = await server.SubmitAsync(
            RequestFrame.WithPayload(
                41,
                "agent.answer",
                new { turnId = "turn_keys", approved = true }
            )
        );
        Assert.False(ambiguous.Ok);
        Assert.Equal("NOT_FOUND", ambiguous.Error!.Code);

        var wrong = await server.SubmitAsync(
            RequestFrame.WithPayload(
                42,
                "agent.answer",
                new
                {
                    turnId = "turn_keys",
                    toolCallId = "call_z",
                    approved = true
                }
            )
        );
        Assert.False(wrong.Ok);
        Assert.Equal("NOT_FOUND", wrong.Error!.Code);

        var right = await server.SubmitAsync(
            RequestFrame.WithPayload(
                43,
                "agent.answer",
                new
                {
                    turnId = "turn_keys",
                    toolCallId = "call_a",
                    approved = true
                }
            )
        );
        Assert.True(right.Ok);
        Assert.Equal(1, server.Turns.PendingCountFor("turn_keys"));
    }

    [Fact]
    public async Task AskUserCarriesQuestionsAndTicket()
    {
        await using var server = new FrameServer();
        var gateway = new PlannedGateway();
        gateway.Enqueue(
            EngineLlmEvent.Tool(
                new EngineToolCall(
                    "call_ask",
                    "ask_user",
                    "{\"questions\": [{\"id\": \"q1\", \"question\": \"Ship it?\", \"options\": [{\"label\": \"yes\"}, {\"label\": \"no\"}]}], \"timeoutSec\": 30}"
                )
            )
        );
        gateway.Enqueue(EngineLlmEvent.Text("noted"));
        server.ConfigureGateway(gateway);
        server.ConfigureToolRunner(
            new EngineToolExecutor(
                new FakeBackend(),
                new EngineToolExecutor.Options { DataRoot = TempRoot() }
            )
        );

        string sessionId = await NewSessionAsync(server);
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(51, "agent.prompt", new { sessionId, text = "ask them" })
        );
        Assert.True(prompted.Ok);
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;

        var confirmed = await WaitForAsync(
            server,
            b => FindToolConfirm(b, turnId, "call_ask") is not null
        );
        EventFrame confirm = FindToolConfirm(confirmed, turnId, "call_ask")!;
        Assert.Equal("confirm", PayloadText(confirm, "policy"));
        string ticket = PayloadText(confirm, "ticketId");
        Assert.StartsWith("ask_", ticket);
        Assert.True(
            confirm.Payload.TryGetProperty("questions", out var questions)
                && questions.ValueKind == System.Text.Json.JsonValueKind.Array
                && questions.GetArrayLength() == 1
        );
        Assert.Equal(sessionId, PayloadText(confirm, "sessionId"));

        var mismatch = await server.SubmitAsync(
            RequestFrame.WithPayload(
                52,
                "agent.answer",
                new
                {
                    turnId,
                    toolCallId = "call_ask",
                    approved = true,
                    ticketId = "ask_wrong",
                    answers = "{\"q1\": \"yes\"}"
                }
            )
        );
        Assert.False(mismatch.Ok);
        Assert.Equal("NOT_FOUND", mismatch.Error!.Code);

        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(
                53,
                "agent.answer",
                new
                {
                    turnId,
                    toolCallId = "call_ask",
                    approved = true,
                    ticketId = ticket,
                    answers = "{\"q1\": \"yes\"}"
                }
            )
        );
        Assert.True(answered.Ok);

        var batch = await WaitForAsync(server, b => FindToolEnd(b, turnId, "call_ask") is not null);
        EventFrame end = FindToolEnd(batch, turnId, "call_ask")!;
        Assert.True(PayloadBool(end, "success"));
        Assert.Contains("yes", PayloadText(end, "output"));
        await WaitForAsync(
            server,
            b =>
                b.Frames.Any(f =>
                    string.Equals(f.Kind, "turn.done", StringComparison.Ordinal)
                    && string.Equals(PayloadText(f, "turnId"), turnId, StringComparison.Ordinal)
                )
        );
    }

    [Fact]
    public void AskUserYamlPassthroughKeepsTheTicket()
    {
        var parsed = TurnRunner.ParseAskUserArgs(
            "{\"questionsYaml\": \"- q1: Ship?\", \"timeoutSec\": 5}"
        );
        Assert.Null(parsed.ErrorCode);
        Assert.Equal("- q1: Ship?", parsed.QuestionYaml);
        Assert.Empty(parsed.Questions);
        Assert.Equal(5, parsed.TimeoutSec);
    }

    [Fact]
    public async Task SubRoleDeniesNestedSpawnAndAsk()
    {
        string root = TempRoot();
        var backend = new FakeBackend();
        backend.Handler = (tool, args, ctx, ct) =>
            Task.FromResult(
                new EngineBackendToolResult(true, EngineToolExecutor.SubAgentMarker, null, 0, 1)
            );
        var executor = new EngineToolExecutor(
            backend,
            new EngineToolExecutor.Options { DataRoot = root, AgentRoleResolver = _ => "sub", }
        );

        IReadOnlyList<EngineToolResult> outcomes = await executor.ExecuteAsync(
            "sess_sub",
            "turn_sub",
            new[] { new EngineToolCall("call_spawn", "spawn_subagent", "{\"task\": \"go\"}") },
            CancellationToken.None
        );
        EngineToolResult only = Assert.Single(outcomes);
        Assert.False(only.Success);
        Assert.Equal("TOOL_DENIED", only.ErrorCode);
        Assert.Contains("SUBAGENT_NESTED", only.Output);
        Assert.Empty(backend.Seen);
    }
}
