using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Detached planner loop for prompt turns: budget guard, multi-round gateway
/// streaming with per-slice event forwarding, deferred tool placeholder and
/// terminal failure mapping. The write gate is never held here; ledger and
/// journal locks serialize concurrent traffic on their own.
/// The whole turn runs without a wall-clock cap: only cancel and the budget
/// guard end it. Single-call stalls surface as isolated tool timeouts.
/// </summary>
public sealed class TurnRunner
{
    public const double CharsPerToken = 3.5;
    public const int ImageCharsPerImage = 4800;
    public const int PreviewTextLimit = 2048;

    private readonly IEngineContext _context;
    private readonly SessionLedger _sessions;
    private readonly TurnCoordinator _turns;
    private readonly UsageLedger _usage;

    public TurnRunner(
        IEngineContext context,
        SessionLedger sessions,
        TurnCoordinator turns,
        UsageLedger usage
    )
    {
        _context = context;
        _sessions = sessions;
        _turns = turns;
        _usage = usage;
    }

    /// <summary>
    /// Short repair hint for a failed tool call, carried back to the model.
    /// </summary>
    public static string HintForTool(string functionName, string? errorCode = null)
    {
        if (!string.IsNullOrEmpty(errorCode))
        {
            switch (errorCode)
            {
                case "OLD_STRING_NOT_FOUND":
                    return " Hint: open read_file at the suggested line and copy-paste the anchor verbatim, or use edit_lines(offset,limit).";
                case "ANCHOR_AMBIGUOUS":
                    return " Hint: lengthen the anchor to include the enclosing function name, or use edit_lines.";
                case "NEED_CONFIRM_OVERWRITE":
                    return " Hint: retry write_file with allow_overwrite:true, or use edit_file/edit_lines for partial edits.";
                case "MISSING_LIMIT":
                    return " Hint: set limit = lines to replace (0 = insert).";
                case "ANCHOR_TOO_SHORT":
                    return " Hint: lengthen the anchor to at least 8 non-whitespace chars with context lines, or use edit_lines(offset,limit).";
                case "EXPECTED_HASH_MISMATCH":
                    return " Hint: re-read the file to get the latest hash, then retry.";
                case "CONTENT_TOO_LARGE":
                    return " Hint: split the change into smaller edit_file/edit_lines calls.";
                case "MISSING_REQUIRED_PARAM":
                    return " Hint: re-issue the same call with the missing field added; do not change other fields.";
                case "TYPE_MISMATCH":
                case "ENUM_VIOLATION":
                case "CONSTRAINT_VIOLATION":
                case "UNKNOWN_ARGUMENT":
                    return " Hint: fix the named field per schema and retry; see the received-args echo.";
                case "IMAGE_TOO_LARGE":
                    return " Hint: the image exceeds the per-image limit; compress it or pick a smaller file, then retry.";
                case "NOT_AN_IMAGE":
                    return " Hint: only jpg/png/webp are readable; convert the file first or pick another image.";
                case "IMAGE_MISSING":
                    return " Hint: verify the path with list_dir, then retry with the exact file name.";
                case "PATH_TRAVERSAL":
                    return " Hint: use a path inside the workspace root.";
                default:
                    break;
            }
        }

        return functionName switch
        {
            "edit_file"
                => " Hint: check the anchor whitespace against read_file output, or use edit_lines(offset, limit).",
            "edit_lines"
                => " Hint: check offset/limit against read_file 1-based line numbers before retrying.",
            "write_file"
                => " Hint: verify the parent directory exists (list_dir) and check path spelling before retrying.",
            "read_file"
                => " Hint: use list_dir to confirm the file exists and check relative path spelling.",
            "exec_command"
                => " Hint: prefer plain command syntax for the configured runner and retry.",
            "list_dir" => " Hint: verify the directory path is relative to the workspace root.",
            "grep_search"
                => " Hint: check the search pattern and target directory, then retry with a simpler pattern.",
            _ => " Hint: check tool arguments and retry.",
        };
    }

    /// <summary>
    /// Follow-up draft for a failed tool result (plain text, never executed).
    /// </summary>
    public static string BuildNextActionHint(
        string functionName,
        string? argumentsJson,
        string? errorCode = null
    )
    {
        if (string.IsNullOrEmpty(errorCode))
        {
            return string.Empty;
        }
        if (string.Equals(errorCode, "MISSING_LIMIT", StringComparison.Ordinal))
        {
            return "\nNextAction: retry edit_lines with explicit \"limit\" (replace N lines, or 0 to insert).";
        }
        if (string.Equals(errorCode, "NEED_CONFIRM_OVERWRITE", StringComparison.Ordinal))
        {
            return "\nNextAction: retry the SAME write_file call with \"allow_overwrite\":true added.";
        }
        _ = functionName;
        _ = argumentsJson;
        return string.Empty;
    }

    /// <summary>Shared character-rule token estimate used before the budget guard.</summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int ascii = 0;
        int cjk = 0;
        int other = 0;
        foreach (char c in text)
        {
            if (
                (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0x3000 && c <= 0x303F)
            )
            {
                cjk++;
            }
            else if (c <= 127)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }
        double tokens = (ascii / CharsPerToken) + (cjk * 1.55) + (other * 1.2);
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    /// <summary>
    /// Maps a gateway failure onto the frozen contract reason code.
    /// Every branch is reachable and covered by the loop test suite.
    /// </summary>
    public static (string Code, string Hint) MapFailure(
        string? errorCode,
        int? statusCode,
        string? message
    )
    {
        string code = (errorCode ?? string.Empty).Trim().ToUpperInvariant();
        string body = (message ?? string.Empty).Trim();
        string hintBody = body.Length > 300 ? body.Substring(0, 300) : body;

        bool mentions(string fragment) =>
            code.Contains(fragment, StringComparison.Ordinal)
            || body.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        if (
            statusCode == 401
            || statusCode == 403
            || code.Contains("PROVIDER_AUTH", StringComparison.Ordinal)
            || code.Contains("NO_PROVIDER", StringComparison.Ordinal)
            || code.Contains("NO_API_KEY", StringComparison.Ordinal)
            || code.Contains("UNAUTHORIZED", StringComparison.Ordinal)
            || code is "HTTP_401" or "HTTP_403"
        )
        {
            return ("PROVIDER_AUTH", hintBody);
        }
        if (
            statusCode == 429
            || code is "HTTP_429" or "RATE_LIMITED"
            || code.Contains("RATE_LIMIT", StringComparison.Ordinal)
        )
        {
            string hint = mentions("backpressure") ? "backpressure" : hintBody;
            return ("RATE_LIMITED", hint);
        }
        if (
            statusCode == 408
            || code is "TOOL_TIMEOUT" or "TIMEOUT" or "HTTP_408"
            || code.Contains("TIMEOUT", StringComparison.Ordinal)
            || code.Contains("IDLE", StringComparison.Ordinal)
            || code.Contains("STALL", StringComparison.Ordinal)
        )
        {
            return ("TOOL_TIMEOUT", hintBody);
        }
        if (mentions("backpressure") || mentions("budget"))
        {
            string hint = mentions("backpressure") ? "backpressure" : hintBody;
            return ("RATE_LIMITED", hint);
        }
        if (
            statusCode is >= 500 and <= 599
            || code.Contains("NETWORK", StringComparison.Ordinal)
            || code.Contains("TRANSPORT", StringComparison.Ordinal)
            || code.Contains("GATEWAY", StringComparison.Ordinal)
            || code.Contains("CONNECTION", StringComparison.Ordinal)
            || code.Contains("SOCKET", StringComparison.Ordinal)
            || code is "HTTP_500" or "HTTP_502" or "HTTP_503" or "NETWORK_FAIL"
        )
        {
            return ("NETWORK_FAIL", hintBody);
        }
        return ("INTERNAL", hintBody);
    }

    internal static TimeSpan ResolveIdleTimeout()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable("HALO_STREAM_IDLE_TIMEOUT_MS");
        }
        catch
        {
            return TimeSpan.FromSeconds(180);
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(180);
        }
        if (!long.TryParse(raw.Trim(), out long ms))
        {
            return TimeSpan.FromSeconds(180);
        }
        if (ms == 0)
        {
            return Timeout.InfiniteTimeSpan;
        }
        ms = Math.Max(30_000, ms);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Runs the whole turn outside the frame write gate. Never throws:
    /// every exit lands a terminal event and finishes the coordinator record.
    /// </summary>
    public async Task RunPromptTurnAsync(
        string sessionId,
        string turnId,
        string promptText,
        string? providerId,
        string? modelId,
        string workspaceId,
        CancellationToken turnToken
    )
    {
        IEngineGateway? gateway = _context.Gateway;
        if (gateway is null)
        {
            FailTurn(sessionId, turnId, "INTERNAL", "Gateway is not configured.");
            return;
        }

        int estimated = EstimateTokens(promptText) + EstimateHistoryTokens(sessionId);
        IEngineBudget? budget = _context.Budget;
        if (budget is not null)
        {
            BudgetDecision verdict;
            try
            {
                verdict = budget.Check(sessionId, estimated);
            }
            catch (Exception ex)
            {
                FailTurn(sessionId, turnId, "INTERNAL", TrimHint(ex.Message));
                return;
            }
            if (!verdict.Allowed)
            {
                FailTurn(
                    sessionId,
                    turnId,
                    "RATE_LIMITED",
                    TrimHint(verdict.Hint ?? "budget exceeded")
                );
                return;
            }
        }

        int ceiling = _context.TurnOptions?.MaxIterations ?? 25;
        int maxRounds = ceiling <= 0 ? 100 : Math.Clamp(ceiling, 1, 100);
        int window = _context.TurnOptions?.HistoryWindow ?? 50;
        if (window <= 0)
        {
            window = 50;
        }

        try
        {
            for (int round = 1; round <= maxRounds; round++)
            {
                turnToken.ThrowIfCancellationRequested();
                var request = new EngineLlmRequest(
                    sessionId,
                    turnId,
                    workspaceId,
                    promptText,
                    SnapshotHistory(sessionId, window),
                    providerId,
                    modelId,
                    maxRounds
                );

                RoundResult roundResult = await StreamRoundAsync(
                        gateway,
                        request,
                        sessionId,
                        turnId,
                        turnToken
                    )
                    .ConfigureAwait(false);

                if (roundResult.Failed)
                {
                    return;
                }
                if (roundResult.Cancelled)
                {
                    return;
                }
                if (roundResult.ToolCalls.Count == 0)
                {
                    PersistAssistant(
                        sessionId,
                        turnId,
                        roundResult.Text,
                        roundResult.Reasoning,
                        null,
                        roundResult.Usage
                    );
                    _context.Events.Append("turn.done", new { turnId, sessionId });
                    _turns.Finish(turnId, TurnOutcome.Done);
                    return;
                }

                PersistAssistant(
                    sessionId,
                    turnId,
                    roundResult.Text,
                    roundResult.Reasoning,
                    roundResult.ToolCalls,
                    roundResult.Usage
                );
                bool keepGoing = await DispatchToolsAsync(
                        sessionId,
                        turnId,
                        roundResult.ToolCalls,
                        turnToken
                    )
                    .ConfigureAwait(false);
                if (!keepGoing)
                {
                    return;
                }
            }

            _context.Events.Append(
                "turn.done",
                new
                {
                    turnId,
                    sessionId,
                    truncated = true
                }
            );
            _turns.Finish(turnId, TurnOutcome.Done);
        }
        catch (OperationCanceledException)
        {
            CancelTurn(sessionId, turnId);
        }
        catch (Exception ex)
        {
            FailTurn(sessionId, turnId, "INTERNAL", TrimHint(ex.Message));
        }
    }

    private sealed record RoundResult(
        bool Failed,
        bool Cancelled,
        string Text,
        string? Reasoning,
        IReadOnlyList<EngineToolCall> ToolCalls,
        EngineUsage? Usage
    );

    private async Task<RoundResult> StreamRoundAsync(
        IEngineGateway gateway,
        EngineLlmRequest request,
        string sessionId,
        string turnId,
        CancellationToken turnToken
    )
    {
        var textAcc = new System.Text.StringBuilder();
        var reasoningAcc = new System.Text.StringBuilder();
        var tools = new Dictionary<string, EngineToolCall>(StringComparer.Ordinal);
        var toolOrder = new List<string>();
        EngineUsage? usage = null;
        int sliceCount = 0;
        int probeEvery = _context.TurnOptions?.SliceCheckEvery ?? 8;
        if (probeEvery <= 0)
        {
            probeEvery = 8;
        }

        TimeSpan idleTimeout = ResolveIdleTimeout();

        IAsyncEnumerator<EngineLlmEvent> enumerator;
        try
        {
            enumerator = gateway.StreamAsync(request, turnToken).GetAsyncEnumerator(turnToken);
        }
        catch (OperationCanceledException)
        {
            CancelTurn(sessionId, turnId);
            return new RoundResult(
                Failed: false,
                Cancelled: true,
                string.Empty,
                null,
                Array.Empty<EngineToolCall>(),
                null
            );
        }
        catch (Exception ex)
        {
            var mapped = MapFailure(ex.GetType().Name, null, ex.Message);
            FailTurn(sessionId, turnId, mapped.Code, mapped.Hint);
            return new RoundResult(
                Failed: true,
                Cancelled: false,
                string.Empty,
                null,
                Array.Empty<EngineToolCall>(),
                null
            );
        }

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await MoveNextWithIdleAsync(enumerator, idleTimeout, turnToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
                {
                    CancelTurn(sessionId, turnId);
                    return new RoundResult(
                        Failed: false,
                        Cancelled: true,
                        string.Empty,
                        null,
                        Array.Empty<EngineToolCall>(),
                        null
                    );
                }
                catch (TimeoutException ex)
                {
                    FailTurn(sessionId, turnId, "TOOL_TIMEOUT", TrimHint(ex.Message));
                    return new RoundResult(
                        Failed: true,
                        Cancelled: false,
                        string.Empty,
                        null,
                        Array.Empty<EngineToolCall>(),
                        null
                    );
                }

                if (!moved)
                {
                    break;
                }

                EngineLlmEvent evt = enumerator.Current;
                switch (evt.Kind)
                {
                    case EngineLlmEventKind.TextDelta:
                        if (!string.IsNullOrEmpty(evt.TextDelta))
                        {
                            textAcc.Append(evt.TextDelta);
                            sliceCount++;
                            _context.Events.Append(
                                "text.delta",
                                new
                                {
                                    turnId,
                                    delta = evt.TextDelta,
                                    kind = "text"
                                }
                            );
                            if (sliceCount % probeEvery == 0)
                            {
                                _ = _context.Events.LatestSequence;
                            }
                        }
                        break;
                    case EngineLlmEventKind.ReasoningDelta:
                        if (!string.IsNullOrEmpty(evt.TextDelta))
                        {
                            reasoningAcc.Append(evt.TextDelta);
                            _context.Events.Append(
                                "text.delta",
                                new
                                {
                                    turnId,
                                    delta = evt.TextDelta,
                                    kind = "reasoning"
                                }
                            );
                        }
                        break;
                    case EngineLlmEventKind.ToolCall:
                        if (evt.ToolCall is not null && !string.IsNullOrWhiteSpace(evt.ToolCall.Id))
                        {
                            if (!tools.ContainsKey(evt.ToolCall.Id))
                            {
                                toolOrder.Add(evt.ToolCall.Id);
                            }
                            tools[evt.ToolCall.Id] = evt.ToolCall;
                        }
                        break;
                    case EngineLlmEventKind.Usage:
                        usage = evt.Usage;
                        break;
                    case EngineLlmEventKind.Error:
                    {
                        var mapped = MapFailure(evt.ErrorCode, evt.StatusCode, evt.ErrorMessage);
                        FailTurn(sessionId, turnId, mapped.Code, mapped.Hint);
                        return new RoundResult(
                            Failed: true,
                            Cancelled: false,
                            string.Empty,
                            null,
                            Array.Empty<EngineToolCall>(),
                            null
                        );
                    }
                    default:
                        break;
                }

                if (turnToken.IsCancellationRequested)
                {
                    CancelTurn(sessionId, turnId);
                    return new RoundResult(
                        Failed: false,
                        Cancelled: true,
                        string.Empty,
                        null,
                        Array.Empty<EngineToolCall>(),
                        null
                    );
                }
            }
        }

        if (turnToken.IsCancellationRequested)
        {
            CancelTurn(sessionId, turnId);
            return new RoundResult(
                Failed: false,
                Cancelled: true,
                string.Empty,
                null,
                Array.Empty<EngineToolCall>(),
                null
            );
        }

        string text = textAcc.ToString();
        string? reasoning = reasoningAcc.Length > 0 ? reasoningAcc.ToString() : null;
        var orderedTools = toolOrder
            .Where(id => tools.ContainsKey(id))
            .Select(id => tools[id])
            .ToList();

        _context.Events.Append("text.done", new { turnId, length = text.Length });

        long inputTokens = usage?.InputTokens ?? EstimateTokens(promptOf(request));
        long outputTokens = usage?.OutputTokens ?? EstimateTokens(text);
        long cachedTokens = usage?.CachedTokens ?? 0;
        _usage.Record(inputTokens, outputTokens, cachedTokens);
        _context.Events.Append(
            "usage.tick",
            new
            {
                turnId,
                inputTokens,
                outputTokens,
                cachedTokens,
            }
        );

        return new RoundResult(
            Failed: false,
            Cancelled: false,
            text,
            reasoning,
            orderedTools,
            usage
        );
    }

    private static string promptOf(EngineLlmRequest request) => request.PromptText;

    private static async Task<bool> MoveNextWithIdleAsync(
        IAsyncEnumerator<EngineLlmEvent> enumerator,
        TimeSpan idleTimeout,
        CancellationToken turnToken
    )
    {
        if (idleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        Task<bool> moveTask = enumerator.MoveNextAsync().AsTask();
        Task delayTask = Task.Delay(idleTimeout, turnToken);
        Task finished = await Task.WhenAny(moveTask, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(finished, delayTask))
        {
            turnToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Single response stalled for {(long)idleTimeout.TotalMilliseconds}ms without any slice."
            );
        }
        return await moveTask.ConfigureAwait(false);
    }

    private async Task<bool> DispatchToolsAsync(
        string sessionId,
        string turnId,
        IReadOnlyList<EngineToolCall> calls,
        CancellationToken turnToken
    )
    {
        IEngineToolRunner? runner = _context.ToolRunner;
        if (runner is null)
        {
            foreach (EngineToolCall call in calls)
            {
                turnToken.ThrowIfCancellationRequested();
                string preview = Truncate(call.ArgumentsJson);
                _context.Events.Append(
                    "tool.start",
                    new
                    {
                        turnId,
                        toolCallId = call.Id,
                        toolName = call.Name,
                        argsPreview = preview,
                        policy = "deferred",
                    }
                );
                string output = "[deferred: tool execution lands with the tool station]";
                _context.Events.Append(
                    "tool.end",
                    new
                    {
                        turnId,
                        toolCallId = call.Id,
                        ok = true,
                        success = true,
                        output,
                    }
                );
                _sessions.Append(
                    sessionId,
                    "tool",
                    output,
                    turnId,
                    messageId: null,
                    toolCallId: call.Id,
                    name: call.Name
                );
                await Task.CompletedTask.ConfigureAwait(false);
            }
            return true;
        }

        string workspaceId = ResolveWorkspace(sessionId);
        string agentRole = ResolveAgentRole(sessionId);

        foreach (EngineToolCall rawCall in calls)
        {
            turnToken.ThrowIfCancellationRequested();
            string callId = string.IsNullOrWhiteSpace(rawCall.Id)
                ? Guid.NewGuid().ToString("N")
                : rawCall.Id.Trim();
            string toolName = (rawCall.Name ?? string.Empty).Trim();
            string argsJson = string.IsNullOrWhiteSpace(rawCall.ArgumentsJson)
                ? "{}"
                : rawCall.ArgumentsJson;
            var call = new EngineToolCall(callId, toolName, argsJson);
            string argsPreview = EngineToolPolicy.BuildArgsPreview(argsJson);
            string policy = EngineToolPolicy.PolicyOf(toolName);

            _context.Events.Append(
                "tool.start",
                new
                {
                    turnId,
                    toolCallId = callId,
                    toolName,
                    argsPreview,
                    policy,
                }
            );

            if (string.Equals(toolName, "ask_user", StringComparison.OrdinalIgnoreCase))
            {
                EngineToolResult asked = await RunAskUserAsync(
                        sessionId,
                        turnId,
                        call,
                        agentRole,
                        workspaceId,
                        argsPreview,
                        turnToken
                    )
                    .ConfigureAwait(false);
                EmitToolEnd(turnId, asked);
                AppendToolHistory(sessionId, turnId, call, asked);
                continue;
            }

            if (EngineToolPolicy.NeedsConfirmation(toolName))
            {
                bool? approved = await RequestWriteApprovalAsync(
                        sessionId,
                        turnId,
                        call,
                        workspaceId,
                        argsPreview,
                        turnToken
                    )
                    .ConfigureAwait(false);
                if (approved is null)
                {
                    _context.Events.Append(
                        "turn.cancelled",
                        new
                        {
                            turnId,
                            sessionId,
                            reason = "timeout"
                        }
                    );
                    _turns.Finish(turnId, TurnOutcome.Cancelled);
                    return false;
                }
                if (!approved.Value)
                {
                    turnToken.ThrowIfCancellationRequested();
                    var denied = new EngineToolResult(
                        callId,
                        toolName,
                        false,
                        $"Tool '{toolName}' was rejected by the user.",
                        "TOOL_DENIED",
                        0,
                        null,
                        -1,
                        null,
                        EngineToolPolicy.BuildErrorPayload("TOOL_DENIED", "rejected"),
                        null,
                        null,
                        null,
                        false,
                        null,
                        $"Tool '{toolName}' was rejected by the user."
                    );
                    EmitToolEnd(turnId, denied);
                    AppendToolHistory(sessionId, turnId, call, denied);
                    _context.Events.Append(
                        "turn.cancelled",
                        new
                        {
                            turnId,
                            sessionId,
                            reason = "rejected"
                        }
                    );
                    _turns.Finish(turnId, TurnOutcome.Cancelled);
                    return false;
                }
            }

            EngineToolResult outcome;
            try
            {
                IReadOnlyList<EngineToolResult> single = await runner
                    .ExecuteAsync(sessionId, turnId, new[] { call }, turnToken)
                    .ConfigureAwait(false);
                outcome =
                    single.FirstOrDefault()
                    ?? FailOutcome(
                        callId,
                        toolName,
                        "INTERNAL",
                        "Tool station returned no result."
                    );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FailTurn(sessionId, turnId, "INTERNAL", TrimHint(ex.Message));
                throw new OperationCanceledException();
            }

            EmitProgressDelta(turnId, outcome);
            EmitToolEnd(turnId, outcome);
            AppendToolHistory(sessionId, turnId, call, outcome);
        }

        return true;
    }

    private async Task<bool?> RequestWriteApprovalAsync(
        string sessionId,
        string turnId,
        EngineToolCall call,
        string workspaceId,
        string argsPreview,
        CancellationToken turnToken
    )
    {
        int waitSec = _context.TurnOptions?.ConfirmTimeoutSec ?? 300;
        waitSec = Math.Clamp(waitSec, 1, 3600);
        _context.Events.Append(
            "tool.confirm",
            new
            {
                turnId,
                toolCallId = call.Id,
                toolName = call.Name,
                argsPreview,
                policy = "confirm",
                timeoutSec = waitSec,
                sessionId,
            }
        );
        _turns.RaiseConfirmation(turnId, call.Id, call.Name, ToArgsElement(call.ArgumentsJson));
        ConfirmationAnswer? answer = await _turns
            .WaitForAnswerAsync(turnId, call.Id, TimeSpan.FromSeconds(waitSec), turnToken)
            .ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }
        if (!answer.Approved)
        {
            turnToken.ThrowIfCancellationRequested();
            return false;
        }
        return true;
    }

    private async Task<EngineToolResult> RunAskUserAsync(
        string sessionId,
        string turnId,
        EngineToolCall call,
        string agentRole,
        string workspaceId,
        string argsPreview,
        CancellationToken turnToken
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EngineToolResult Fail(string code, string message)
        {
            sw.Stop();
            string display = EngineToolPolicy.BuildDisplay(message, null, out bool t, out int n);
            return new EngineToolResult(
                call.Id,
                call.Name,
                false,
                display,
                code,
                sw.ElapsedMilliseconds,
                null,
                -1,
                null,
                EngineToolPolicy.BuildErrorPayload(code, message),
                null,
                null,
                null,
                t,
                t ? n : null,
                EngineToolPolicy.BuildPersisted(message)
            );
        }

        if (string.Equals(agentRole, "sub", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "TOOL_DENIED",
                "[SUBAGENT_ASK_DENIED] Nested questions are disabled; "
                    + "continue with a conservative default and note the assumption for the main session."
            );
        }

        var parsed = ParseAskUserArgs(call.ArgumentsJson);
        if (parsed.ErrorCode is not null)
        {
            return Fail(parsed.ErrorCode, parsed.Error ?? "ask_user arguments are invalid.");
        }

        int waitSec = Math.Clamp(parsed.TimeoutSec, 1, 3600);
        string ticketId = "ask_" + Guid.NewGuid().ToString("N").Substring(0, 16);
        _context.Events.Append(
            "tool.confirm",
            new
            {
                turnId,
                toolCallId = call.Id,
                toolName = call.Name,
                argsPreview,
                policy = "confirm",
                timeoutSec = waitSec,
                questions = parsed.Questions.Select(q => new
                {
                    id = q.Id,
                    question = q.Question,
                    options = q.Options.Select(o => new
                    {
                        label = o.Label,
                        description = o.Description
                    }),
                    multiSelect = q.MultiSelect,
                    allowCustom = q.AllowCustom,
                    @default = q.Default,
                }),
                questionYaml = parsed.QuestionYaml,
                ticketId,
                sessionId,
            }
        );
        _turns.RaiseConfirmation(
            turnId,
            call.Id,
            call.Name,
            ToArgsElement(call.ArgumentsJson),
            ticketId
        );
        ConfirmationAnswer? answer = await _turns
            .WaitForAnswerAsync(turnId, call.Id, TimeSpan.FromSeconds(waitSec), turnToken)
            .ConfigureAwait(false);
        if (answer is null)
        {
            return Fail(
                "TOOL_TIMEOUT",
                $"Waiting for the user answer timed out ({waitSec}s, ticket {ticketId}); "
                    + "continue with a conservative default and ask again with fewer questions if still blocked."
            );
        }
        if (!answer.Approved || turnToken.IsCancellationRequested)
        {
            turnToken.ThrowIfCancellationRequested();
            return Fail(
                "TOOL_DENIED",
                "The question was dismissed by the user; "
                    + "continue with a conservative default instead of asking again."
            );
        }

        string answersJson = string.IsNullOrWhiteSpace(answer.AnswersJson)
            ? "{}"
            : answer.AnswersJson;
        string output = BuildAskOutput(parsed.Questions, answersJson, ticketId);
        sw.Stop();
        string display = EngineToolPolicy.BuildDisplay(
            output,
            null,
            out bool truncated,
            out int omitted
        );
        return new EngineToolResult(
            call.Id,
            call.Name,
            true,
            display,
            null,
            sw.ElapsedMilliseconds,
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            truncated,
            truncated ? omitted : null,
            EngineToolPolicy.BuildPersisted(output)
        );
    }

    private void EmitProgressDelta(string turnId, EngineToolResult outcome)
    {
        int fullLength = outcome.TruncatedChars ?? outcome.Output.Length;
        if (!outcome.Truncated && fullLength <= EngineToolPolicy.PersistLimit)
        {
            return;
        }
        try
        {
            _context.Events.Append(
                "tool.delta",
                new
                {
                    turnId,
                    toolCallId = outcome.ToolCallId,
                    delta = $"[progress] {fullLength} chars received; card shows the preview"
                        + (
                            string.IsNullOrWhiteSpace(outcome.SpilledFile)
                                ? "."
                                : $" (full text: {outcome.SpilledFile})."
                        ),
                }
            );
        }
        catch { }
    }

    private void EmitToolEnd(string turnId, EngineToolResult outcome)
    {
        _context.Events.Append(
            "tool.end",
            new
            {
                turnId,
                toolCallId = outcome.ToolCallId,
                ok = outcome.Success,
                success = outcome.Success,
                output = outcome.Output,
                exitCode = outcome.ExitCode,
                durationMs = outcome.DurationMs,
                fileBytes = outcome.FileBytes,
                errorCode = outcome.ErrorCode,
                errorPayload = outcome.ErrorPayloadJson,
                spilledFile = outcome.SpilledFile,
                images = outcome.Images?.Select(i => new
                {
                    fileName = i.FileName,
                    relPath = i.RelPath,
                    mime = i.Mime,
                    width = i.Width,
                    height = i.Height,
                    sizeBytes = i.SizeBytes,
                    isMissing = i.IsMissing,
                }),
                validatedWarnings = outcome.ValidatedWarnings,
                truncated = outcome.Truncated,
                truncatedChars = outcome.TruncatedChars,
            }
        );
    }

    private void AppendToolHistory(
        string sessionId,
        string turnId,
        EngineToolCall call,
        EngineToolResult outcome
    )
    {
        string persisted = outcome.PersistedText ?? outcome.Output;
        IReadOnlyList<SessionImageReference>? images = outcome
            .Images?.Select(i => new SessionImageReference(
                string.IsNullOrWhiteSpace(i.FileName) ? i.RelPath : i.FileName,
                string.IsNullOrWhiteSpace(i.FileName) ? i.RelPath : i.FileName,
                i.Mime,
                i.RelPath,
                i.Width,
                i.Height,
                i.SizeBytes,
                i.IsMissing
            ))
            .ToList();
        string? warnings = outcome.ValidatedWarnings is { Count: > 0 }
            ? string.Join("; ", outcome.ValidatedWarnings)
            : null;
        _sessions.Append(
            sessionId,
            "tool",
            persisted,
            turnId,
            messageId: null,
            toolCallId: outcome.ToolCallId,
            name: outcome.ToolName,
            images: images,
            validatedWarnings: warnings,
            errorCode: outcome.ErrorCode
        );
    }

    private static EngineToolResult FailOutcome(
        string callId,
        string toolName,
        string code,
        string message
    )
    {
        string display = EngineToolPolicy.BuildDisplay(message, null, out bool t, out int n);
        return new EngineToolResult(
            callId,
            toolName,
            false,
            display,
            code,
            0,
            null,
            -1,
            null,
            EngineToolPolicy.BuildErrorPayload(code, message),
            null,
            null,
            null,
            t,
            t ? n : null,
            EngineToolPolicy.BuildPersisted(message)
        );
    }

    private string ResolveWorkspace(string sessionId)
    {
        try
        {
            if (_sessions.TryGetDescriptor(sessionId, out var descriptor) && descriptor is not null)
            {
                return string.IsNullOrWhiteSpace(descriptor.WorkspaceId)
                    ? "default"
                    : descriptor.WorkspaceId;
            }
        }
        catch { }
        return "default";
    }

    private string ResolveAgentRole(string sessionId)
    {
        try
        {
            if (_sessions.TryGetDescriptor(sessionId, out var descriptor) && descriptor is not null)
            {
                return string.IsNullOrWhiteSpace(descriptor.AgentRole)
                    ? "main"
                    : descriptor.AgentRole;
            }
        }
        catch { }
        return "main";
    }

    private static JsonElement ToArgsElement(string? argumentsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson
            );
            return doc.RootElement.Clone();
        }
        catch
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    public sealed record AskOption(string Label, string? Description);

    public sealed record AskQuestion(
        string Id,
        string Question,
        List<AskOption> Options,
        bool MultiSelect,
        bool AllowCustom,
        string? Default
    );

    public sealed record AskParseResult(
        List<AskQuestion> Questions,
        string? QuestionYaml,
        int TimeoutSec,
        string? ErrorCode,
        string? Error
    );

    public static AskParseResult ParseAskUserArgs(string? argumentsJson)
    {
        const int defaultTimeout = 300;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new AskParseResult(
                new List<AskQuestion>(),
                null,
                defaultTimeout,
                "MISSING_REQUIRED_PARAM",
                "ask_user requires at least one question: pass question, questions or questionsYaml."
            );
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argumentsJson.Trim());
            root = doc.RootElement.Clone();
        }
        catch
        {
            return new AskParseResult(
                new List<AskQuestion>(),
                null,
                defaultTimeout,
                "INVALID_JSON_ARGUMENTS",
                "ask_user arguments must be a JSON object string."
            );
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AskParseResult(
                new List<AskQuestion>(),
                null,
                defaultTimeout,
                "INVALID_JSON_ARGUMENTS",
                "ask_user arguments must be a JSON object string."
            );
        }

        int timeout = defaultTimeout;
        if (
            root.TryGetProperty("timeoutSec", out JsonElement timeoutNode)
            && timeoutNode.ValueKind == JsonValueKind.Number
            && timeoutNode.TryGetInt32(out int parsedTimeout)
        )
        {
            timeout = Math.Clamp(parsedTimeout, 1, 3600);
        }

        if (
            root.TryGetProperty("questionsYaml", out JsonElement yamlNode)
            && yamlNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(yamlNode.GetString())
        )
        {
            return new AskParseResult(
                new List<AskQuestion>(),
                yamlNode.GetString()!.Trim(),
                timeout,
                null,
                null
            );
        }

        if (root.TryGetProperty("questions", out JsonElement questionsNode))
        {
            if (questionsNode.ValueKind == JsonValueKind.String)
            {
                string raw = (questionsNode.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return MissingQuestions(timeout);
                }
                if (raw.StartsWith("[") || raw.StartsWith("{"))
                {
                    try
                    {
                        using JsonDocument inner = JsonDocument.Parse(raw);
                        JsonElement payload = inner.RootElement.Clone();
                        if (payload.ValueKind == JsonValueKind.Array)
                        {
                            return BuildQuestions(payload, timeout);
                        }
                        return BuildQuestions(payload, timeout, single: true);
                    }
                    catch
                    {
                        return InvalidQuestions(timeout);
                    }
                }
                return new AskParseResult(new List<AskQuestion>(), raw, timeout, null, null);
            }
            if (questionsNode.ValueKind == JsonValueKind.Array)
            {
                return BuildQuestions(questionsNode, timeout);
            }
            return InvalidQuestions(timeout);
        }

        if (root.TryGetProperty("question", out JsonElement singleNode))
        {
            if (singleNode.ValueKind == JsonValueKind.String)
            {
                string text = (singleNode.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return MissingQuestions(timeout);
                }
                return new AskParseResult(
                    new List<AskQuestion>
                    {
                        new("q1", text, new List<AskOption>(), false, true, null),
                    },
                    null,
                    timeout,
                    null,
                    null
                );
            }
            if (singleNode.ValueKind == JsonValueKind.Object)
            {
                return BuildQuestions(singleNode, timeout, single: true);
            }
            return InvalidQuestions(timeout);
        }

        return MissingQuestions(timeout);
    }

    private static AskParseResult MissingQuestions(int timeout) =>
        new(
            new List<AskQuestion>(),
            null,
            timeout,
            "MISSING_REQUIRED_PARAM",
            "ask_user requires at least one question: pass question, questions or questionsYaml."
        );

    private static AskParseResult InvalidQuestions(int timeout) =>
        new(
            new List<AskQuestion>(),
            null,
            timeout,
            "CONSTRAINT_VIOLATION",
            "questions must be an array of 1-4 {id, question, options?} objects or a YAML string."
        );

    private static AskParseResult BuildQuestions(JsonElement node, int timeout, bool single = false)
    {
        var items = new List<JsonElement>();
        if (single)
        {
            items.Add(node);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(node.EnumerateArray());
        }
        else
        {
            return InvalidQuestions(timeout);
        }

        if (items.Count == 0 || items.Count > 4)
        {
            return InvalidQuestions(timeout);
        }

        var questions = new List<AskQuestion>();
        int index = 0;
        foreach (JsonElement item in items)
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                return InvalidQuestions(timeout);
            }
            string id =
                item.TryGetProperty("id", out JsonElement idNode)
                && idNode.ValueKind == JsonValueKind.String
                    ? (idNode.GetString() ?? string.Empty).Trim()
                    : string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                id = "q" + index;
            }
            string text =
                item.TryGetProperty("question", out JsonElement textNode)
                && textNode.ValueKind == JsonValueKind.String
                    ? (textNode.GetString() ?? string.Empty).Trim()
                    : string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return new AskParseResult(
                    new List<AskQuestion>(),
                    null,
                    timeout,
                    "MISSING_REQUIRED_PARAM",
                    $"question {id} is empty; every question needs text."
                );
            }
            var options = new List<AskOption>();
            if (
                item.TryGetProperty("options", out JsonElement optionsNode)
                && optionsNode.ValueKind == JsonValueKind.Array
            )
            {
                foreach (JsonElement option in optionsNode.EnumerateArray())
                {
                    if (option.ValueKind == JsonValueKind.String)
                    {
                        string label = (option.GetString() ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(label))
                        {
                            options.Add(new AskOption(label, null));
                        }
                    }
                    else if (option.ValueKind == JsonValueKind.Object)
                    {
                        string label =
                            option.TryGetProperty("label", out JsonElement labelNode)
                            && labelNode.ValueKind == JsonValueKind.String
                                ? (labelNode.GetString() ?? string.Empty).Trim()
                                : string.Empty;
                        if (string.IsNullOrEmpty(label))
                        {
                            continue;
                        }
                        string? description =
                            option.TryGetProperty("description", out JsonElement descNode)
                            && descNode.ValueKind == JsonValueKind.String
                                ? descNode.GetString()
                                : null;
                        options.Add(new AskOption(label, description));
                    }
                }
                if (options.Count > 6)
                {
                    return new AskParseResult(
                        new List<AskQuestion>(),
                        null,
                        timeout,
                        "CONSTRAINT_VIOLATION",
                        $"question {id} carries more than 6 options; trim to 6 or fewer."
                    );
                }
            }
            bool multiSelect =
                item.TryGetProperty("multiSelect", out JsonElement multiNode)
                && multiNode.ValueKind == JsonValueKind.True;
            string? fallback =
                item.TryGetProperty("default", out JsonElement defaultNode)
                && defaultNode.ValueKind == JsonValueKind.String
                    ? defaultNode.GetString()
                    : null;
            questions.Add(new AskQuestion(id, text, options, multiSelect, true, fallback));
        }

        return new AskParseResult(questions, null, timeout, null, null);
    }

    internal static string BuildAskOutput(
        List<AskQuestion> questions,
        string answersJson,
        string ticketId
    )
    {
        string answers = string.IsNullOrWhiteSpace(answersJson) ? "{}" : answersJson.Trim();
        if (answers.Length > 2000)
        {
            answers = answers.Substring(0, 2000) + " … [answers truncated]";
        }
        if (questions.Count == 0)
        {
            return $"User answered (ticket {ticketId}):\n```json\n{answers}\n```";
        }
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"User answered {questions.Count} question(s) (ticket {ticketId}):");
        foreach (AskQuestion question in questions)
        {
            lines.AppendLine($"- {question.Id}: {question.Question}");
        }
        lines.AppendLine("```json");
        lines.AppendLine(answers);
        lines.AppendLine("```");
        return lines.ToString().TrimEnd();
    }

    private void PersistAssistant(
        string sessionId,
        string turnId,
        string text,
        string? reasoning,
        IReadOnlyList<EngineToolCall>? toolCalls,
        EngineUsage? usage
    )
    {
        IReadOnlyList<SessionToolCall>? rows = toolCalls is null
            ? null
            : toolCalls.Select(c => new SessionToolCall(c.Id, c.Name, c.ArgumentsJson)).ToList();
        _sessions.Append(
            sessionId,
            "assistant",
            text,
            turnId,
            messageId: null,
            toolCalls: rows,
            inputTokens: usage?.InputTokens,
            outputTokens: usage?.OutputTokens,
            cachedTokens: usage?.CachedTokens,
            reasoningContent: reasoning
        );
    }

    private IReadOnlyList<EngineHistoryMessage> SnapshotHistory(string sessionId, int window)
    {
        try
        {
            var rows = _sessions.Snapshot(sessionId);
            int skip = Math.Max(0, rows.Count - window);
            return rows.Skip(skip).Select(m => new EngineHistoryMessage(m.Role, m.Text)).ToList();
        }
        catch
        {
            return Array.Empty<EngineHistoryMessage>();
        }
    }

    private int EstimateHistoryTokens(string sessionId)
    {
        try
        {
            var rows = _sessions.Snapshot(sessionId);
            int chars = 0;
            foreach (var row in rows)
            {
                chars += row.Text?.Length ?? 0;
                if (row.Images is not null)
                {
                    chars += row.Images.Count * ImageCharsPerImage;
                }
            }
            return Math.Max(0, (int)Math.Ceiling(chars / CharsPerToken));
        }
        catch
        {
            return 0;
        }
    }

    private void FailTurn(string sessionId, string turnId, string code, string hint)
    {
        try
        {
            _context.Events.Append(
                "turn.failed",
                new
                {
                    turnId,
                    sessionId,
                    reason = code,
                    hint = TrimHint(hint),
                }
            );
        }
        catch { }
        try
        {
            _turns.Finish(turnId, TurnOutcome.Failed);
        }
        catch { }
    }

    private void CancelTurn(string sessionId, string turnId)
    {
        try
        {
            _context.Events.Append(
                "turn.cancelled",
                new
                {
                    turnId,
                    sessionId,
                    reason = "cancelled",
                    signaled = true,
                }
            );
        }
        catch { }
        try
        {
            _turns.Finish(turnId, TurnOutcome.Cancelled);
        }
        catch { }
    }

    internal static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        if (text.Length <= PreviewTextLimit)
        {
            return text;
        }
        return text.Substring(0, PreviewTextLimit);
    }

    internal static string TrimHint(string? hint)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return string.Empty;
        }
        return hint.Length > 300 ? hint.Substring(0, 300) : hint;
    }
}
