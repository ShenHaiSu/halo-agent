namespace HaloAgent.Engine.Abstractions;

/// <summary>One history row handed to the language gateway for prompt building.</summary>
public sealed record EngineHistoryMessage(string Role, string Text);

/// <summary>Assistant-planned tool invocation streamed back by the gateway.</summary>
public sealed record EngineToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Token usage reported by the gateway for one streamed round.</summary>
public sealed record EngineUsage(long InputTokens, long OutputTokens, long CachedTokens);

/// <summary>Streaming event kinds produced by the engine gateway.</summary>
public enum EngineLlmEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCall,
    Usage,
    Error,
}

/// <summary>Single streaming event from the language gateway.</summary>
public sealed record EngineLlmEvent(
    EngineLlmEventKind Kind,
    string? TextDelta = null,
    EngineToolCall? ToolCall = null,
    EngineUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? StatusCode = null
)
{
    public static EngineLlmEvent Text(string delta) =>
        new(EngineLlmEventKind.TextDelta, TextDelta: delta);

    public static EngineLlmEvent Reasoning(string delta) =>
        new(EngineLlmEventKind.ReasoningDelta, TextDelta: delta);

    public static EngineLlmEvent Tool(EngineToolCall call) =>
        new(EngineLlmEventKind.ToolCall, ToolCall: call);

    public static EngineLlmEvent UsageUpdate(EngineUsage usage) =>
        new(EngineLlmEventKind.Usage, Usage: usage);

    public static EngineLlmEvent Failure(string code, string message, int? statusCode = null) =>
        new(
            EngineLlmEventKind.Error,
            ErrorCode: code,
            ErrorMessage: message,
            StatusCode: statusCode
        );
}

/// <summary>Gateway request built from the engine ledger for one turn.</summary>
public sealed record EngineLlmRequest(
    string SessionId,
    string TurnId,
    string WorkspaceId,
    string PromptText,
    IReadOnlyList<EngineHistoryMessage> History,
    string? ProviderId = null,
    string? ModelId = null,
    int MaxIterations = 25
);

/// <summary>
/// Language gateway port owned by the host shell.
/// The engine streams through this interface so tests can substitute a scripted mock
/// while the desktop host forwards to the real provider pipeline with shared
/// connection reuse. No framework or UI dependency may leak through here.
/// </summary>
public interface IEngineGateway
{
    IAsyncEnumerable<EngineLlmEvent> StreamAsync(
        EngineLlmRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Budget verdict for one turn before any gateway traffic.</summary>
public sealed record BudgetDecision(bool Allowed, string? Hint = null);

/// <summary>
/// Token budget port owned by the host shell.
/// Estimates use the shared character rule so ledger and gateway agree on size.
/// </summary>
public interface IEngineBudget
{
    BudgetDecision Check(string sessionId, int estimatedTokens);
}

/// <summary>Turn loop tuning owned by the engine with host-supplied overrides.</summary>
public sealed class EngineTurnOptions
{
    /// <summary>Planner loop ceiling, clamped to 1..100 by the runner.</summary>
    public int MaxIterations { get; set; } = 25;

    /// <summary>Journal sequence probe cadence while forwarding slices.</summary>
    public int SliceCheckEvery { get; set; } = 8;

    /// <summary>History rows offered to the gateway per round.</summary>
    public int HistoryWindow { get; set; } = 50;
}

/// <summary>Tool outcome produced by the tool station (filled in by the later tool work).</summary>
public sealed record EngineToolResult(
    string ToolCallId,
    string ToolName,
    bool Success,
    string Output,
    string? ErrorCode = null,
    long DurationMs = 0,
    int? TimeoutMs = null
);

/// <summary>
/// Tool execution seam for the planner loop.
/// The true loop station ships with a deferred placeholder; the tool station
/// replaces it with real dispatch without touching the loop itself.
/// </summary>
public interface IEngineToolRunner
{
    Task<IReadOnlyList<EngineToolResult>> ExecuteAsync(
        string sessionId,
        string turnId,
        IReadOnlyList<EngineToolCall> calls,
        CancellationToken cancellationToken
    );
}
