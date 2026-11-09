using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// True tool station for the planner loop: dispatches every model-planned call
/// through the host backend, then shapes the raw outcome into card, history and
/// model facing fields. Unknown tools stay inside TOOL_DENIED and never leak
/// into the cutover NOT_FOUND path; slow calls surface as isolated TOOL_TIMEOUT
/// without failing the turn; oversized output spills to the session temp dir.
/// This runner never throws: every exit is an <see cref="EngineToolResult"/>.
/// </summary>
public sealed class EngineToolExecutor : IEngineToolRunner
{
    public sealed class Options
    {
        public string DataRoot { get; set; } = string.Empty;
        public int DefaultTimeoutMs { get; set; } = EngineToolPolicy.ExecDefaultTimeoutMs;
        public int MaxTimeoutMs { get; set; } = EngineToolPolicy.ExecMaxTimeoutMs;
        public Func<string, string>? WorkspaceRootResolver { get; set; }
        public Func<string, string>? WorkspaceResolver { get; set; }
        public Func<string, string>? AgentRoleResolver { get; set; }
        public Func<string, string?>? ParentSessionResolver { get; set; }
    }

    public const string SubAgentMarker = "[SUBAGENT_INTERNAL]";

    private readonly IEngineToolBackend _backend;
    private readonly Options _options;

    public EngineToolExecutor(IEngineToolBackend backend, Options? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? new Options();
    }

    public async Task<IReadOnlyList<EngineToolResult>> ExecuteAsync(
        string sessionId,
        string turnId,
        IReadOnlyList<EngineToolCall> calls,
        CancellationToken cancellationToken
    )
    {
        var outcomes = new List<EngineToolResult>(calls?.Count ?? 0);
        if (calls is null || calls.Count == 0)
        {
            return outcomes;
        }
        foreach (EngineToolCall call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(
                await ExecuteOneAsync(sessionId, call, cancellationToken).ConfigureAwait(false)
            );
        }
        _ = turnId;
        return outcomes;
    }

    private async Task<EngineToolResult> ExecuteOneAsync(
        string sessionId,
        EngineToolCall call,
        CancellationToken turnToken
    )
    {
        var sw = Stopwatch.StartNew();
        string callId = string.IsNullOrWhiteSpace(call?.Id)
            ? Guid.NewGuid().ToString("N")
            : call.Id.Trim();
        string toolName = (call?.Name ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(toolName))
        {
            sw.Stop();
            return Denied(
                callId,
                toolName,
                "UNKNOWN_TOOL",
                "Tool name is empty; the planner must name one of the declared tools.",
                sw.ElapsedMilliseconds
            );
        }

        string agentRole = ResolveAgentRole(sessionId);
        if (
            string.Equals(toolName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
            && string.Equals(agentRole, "sub", StringComparison.OrdinalIgnoreCase)
        )
        {
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "TOOL_DENIED",
                "[SUBAGENT_NESTED_DENIED] Nested subagents are disabled; "
                    + "continue with the current session and note the assumption for the main session.",
                sw.ElapsedMilliseconds,
                exitCode: -1
            );
        }

        string? rawArgs = call?.ArgumentsJson;
        string argsJson = string.IsNullOrWhiteSpace(rawArgs) ? "{}" : rawArgs.Trim();
        if (!IsJsonObject(argsJson))
        {
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "INVALID_JSON_ARGUMENTS",
                "Tool arguments must be a JSON object string.",
                sw.ElapsedMilliseconds,
                exitCode: -1
            );
        }

        int? timeoutMs = ResolveExecTimeout(toolName, argsJson);
        if (timeoutMs == int.MinValue)
        {
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "INVALID_TIMEOUT",
                $"timeout_ms for exec_command must stay within {EngineToolPolicy.ExecMinTimeoutMs}..{EngineToolPolicy.ExecMaxTimeoutMs} milliseconds.",
                sw.ElapsedMilliseconds,
                exitCode: -1
            );
        }

        string workspaceId = ResolveWorkspace(sessionId);
        var backendContext = new EngineToolBackendContext(
            sessionId,
            callId,
            workspaceId,
            ResolveWorkspaceRoot(sessionId),
            _options.DataRoot ?? string.Empty,
            agentRole,
            ResolveParentSession(sessionId),
            ClosedWorld: true
        );

        int waitMs = Math.Clamp(
            timeoutMs ?? _options.DefaultTimeoutMs,
            EngineToolPolicy.ExecMinTimeoutMs,
            _options.MaxTimeoutMs > 0 ? _options.MaxTimeoutMs : EngineToolPolicy.ExecMaxTimeoutMs
        );

        EngineBackendToolResult raw;
        try
        {
            using var timeoutScope = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                turnToken,
                timeoutScope.Token
            );
            Task<EngineBackendToolResult> invoke = _backend.InvokeAsync(
                toolName,
                argsJson,
                backendContext,
                linked.Token
            );
            Task finished = await Task.WhenAny(invoke, Task.Delay(waitMs, turnToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(finished, invoke))
            {
                turnToken.ThrowIfCancellationRequested();
                try
                {
                    timeoutScope.Cancel();
                }
                catch { }
                sw.Stop();
                return FailResult(
                    callId,
                    toolName,
                    "TOOL_TIMEOUT",
                    $"Tool '{toolName}' exceeded {waitMs}ms and was isolated; narrow the arguments and retry.",
                    sw.ElapsedMilliseconds,
                    exitCode: -1,
                    timeoutMs: waitMs
                );
            }
            raw = await invoke.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            turnToken.ThrowIfCancellationRequested();
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "TOOL_TIMEOUT",
                $"Tool '{toolName}' was cancelled by its own timeout guard; narrow the arguments and retry.",
                sw.ElapsedMilliseconds,
                exitCode: -1,
                timeoutMs: waitMs
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "INTERNAL",
                TrimHint(ex.Message),
                sw.ElapsedMilliseconds,
                exitCode: -1
            );
        }

        if (raw is null)
        {
            sw.Stop();
            return FailResult(
                callId,
                toolName,
                "INTERNAL",
                $"Tool '{toolName}' returned no result; retry with narrower arguments.",
                sw.ElapsedMilliseconds,
                exitCode: -1
            );
        }

        sw.Stop();
        long durationMs = raw.DurationMs > 0 ? raw.DurationMs : sw.ElapsedMilliseconds;
        return ShapeSuccess(callId, toolName, raw, sessionId, workspaceId, durationMs, timeoutMs);
    }

    private EngineToolResult ShapeSuccess(
        string callId,
        string toolName,
        EngineBackendToolResult raw,
        string sessionId,
        string workspaceId,
        long durationMs,
        int? timeoutMs
    )
    {
        if (!raw.Success)
        {
            string code = string.IsNullOrWhiteSpace(raw.ErrorCode)
                ? "INTERNAL"
                : raw.ErrorCode.Trim();
            string detail = string.IsNullOrWhiteSpace(raw.Error)
                ? $"Tool '{toolName}' failed with {code}."
                : raw.Error;
            string payload = string.IsNullOrWhiteSpace(raw.ErrorPayloadJson)
                ? EngineToolPolicy.BuildErrorPayload(code, detail)
                : raw.ErrorPayloadJson;
            string output = string.IsNullOrWhiteSpace(raw.Output) ? detail : raw.Output;
            string display = EngineToolPolicy.BuildDisplay(
                output,
                raw.SpilledFile,
                out bool t,
                out int n
            );
            return new EngineToolResult(
                callId,
                toolName,
                false,
                display,
                code,
                durationMs,
                timeoutMs,
                raw.ExitCode,
                raw.FileBytes,
                payload,
                raw.SpilledFile,
                raw.Images,
                raw.ValidatedWarnings,
                t,
                t ? n : null,
                EngineToolPolicy.BuildPersisted(output)
            );
        }

        string text = raw.Output ?? string.Empty;
        if (
            string.Equals(toolName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
            && !text.Contains(SubAgentMarker, StringComparison.Ordinal)
        )
        {
            text = string.IsNullOrEmpty(text) ? SubAgentMarker : SubAgentMarker + "\n" + text;
        }
        if (string.IsNullOrEmpty(text))
        {
            text = "(no output)";
        }

        string? spilled = raw.SpilledFile;
        if (string.IsNullOrWhiteSpace(spilled) && text.Length > EngineToolPolicy.SpillThreshold)
        {
            spilled = SpillToSessionTemp(sessionId, workspaceId, callId, text);
        }
        string shaped = EngineToolPolicy.BuildDisplay(
            text,
            spilled,
            out bool truncated,
            out int omitted
        );
        return new EngineToolResult(
            callId,
            toolName,
            true,
            shaped,
            null,
            durationMs,
            timeoutMs,
            raw.ExitCode,
            raw.FileBytes,
            raw.ErrorPayloadJson,
            spilled,
            raw.Images,
            raw.ValidatedWarnings,
            truncated,
            truncated ? omitted : null,
            EngineToolPolicy.BuildPersisted(text)
        );
    }

    private static EngineToolResult Denied(
        string callId,
        string toolName,
        string code,
        string detail,
        long durationMs
    ) => FailResult(callId, toolName, code, detail, durationMs, exitCode: -1);

    private static EngineToolResult FailResult(
        string callId,
        string toolName,
        string code,
        string detail,
        long durationMs,
        int exitCode,
        int? timeoutMs = null
    )
    {
        string shaped = EngineToolPolicy.BuildDisplay(detail, null, out bool t, out int n);
        return new EngineToolResult(
            callId,
            toolName,
            false,
            shaped,
            code,
            durationMs,
            timeoutMs,
            exitCode,
            null,
            EngineToolPolicy.BuildErrorPayload(code, detail),
            null,
            null,
            null,
            t,
            t ? n : null,
            EngineToolPolicy.BuildPersisted(detail)
        );
    }

    private string? SpillToSessionTemp(
        string sessionId,
        string workspaceId,
        string callId,
        string text
    )
    {
        try
        {
            string root = string.IsNullOrWhiteSpace(_options.DataRoot)
                ? Path.GetTempPath()
                : _options.DataRoot;
            string safeCall = EngineToolPolicy.IsValidCallId(callId) ? callId.Trim() : "tool";
            string dir = Path.Combine(
                root,
                "sessions",
                workspaceId,
                sessionId,
                "tmp",
                "tool-outputs"
            );
            Directory.CreateDirectory(dir);
            string full = Path.Combine(dir, $"call_{safeCall}.txt");
            File.WriteAllText(full, text);
            try
            {
                return Path.GetRelativePath(root, full).Replace('\\', '/');
            }
            catch
            {
                return full.Replace('\\', '/');
            }
        }
        catch
        {
            return null;
        }
    }

    private int? ResolveExecTimeout(string toolName, string argsJson)
    {
        if (!string.Equals(toolName, "exec_command", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("timeout_ms", out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt32(out int value)
            )
            {
                if (
                    value < EngineToolPolicy.ExecMinTimeoutMs
                    || value > EngineToolPolicy.ExecMaxTimeoutMs
                )
                {
                    return int.MinValue;
                }
                return value;
            }
        }
        catch { }
        return null;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private string ResolveWorkspace(string sessionId)
    {
        try
        {
            string? workspace = _options.WorkspaceResolver?.Invoke(sessionId);
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                return workspace.Trim();
            }
        }
        catch { }
        return "default";
    }

    private string ResolveWorkspaceRoot(string sessionId)
    {
        try
        {
            string? root = _options.WorkspaceRootResolver?.Invoke(sessionId);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root.Trim();
            }
        }
        catch { }
        return string.Empty;
    }

    private string ResolveAgentRole(string sessionId)
    {
        try
        {
            string? role = _options.AgentRoleResolver?.Invoke(sessionId);
            if (!string.IsNullOrWhiteSpace(role))
            {
                return role.Trim();
            }
        }
        catch { }
        return "main";
    }

    private string? ResolveParentSession(string sessionId)
    {
        try
        {
            return _options.ParentSessionResolver?.Invoke(sessionId);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimHint(string? hint)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return string.Empty;
        }
        return hint.Length > 300 ? hint.Substring(0, 300) : hint;
    }
}
