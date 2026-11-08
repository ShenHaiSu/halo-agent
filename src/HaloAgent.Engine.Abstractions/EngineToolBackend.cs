namespace HaloAgent.Engine.Abstractions;

/// <summary>Image slot carried on a tool result; bytes stay in storage, history keeps the digest path.</summary>
public sealed record EngineImageReference(
    string FileName,
    string RelPath,
    string Mime = "",
    int Width = 0,
    int Height = 0,
    long SizeBytes = 0,
    bool IsMissing = false
);

/// <summary>Execution context handed to the host-side tool backend for one call.</summary>
public sealed record EngineToolBackendContext(
    string SessionId,
    string ToolCallId,
    string WorkspaceId,
    string WorkspaceRoot,
    string DataRoot,
    string AgentRole = "main",
    string? ParentSessionId = null,
    bool ClosedWorld = true
);

/// <summary>Raw tool outcome produced by the host backend before engine shaping.</summary>
public sealed record EngineBackendToolResult(
    bool Success,
    string Output,
    string? Error,
    int ExitCode,
    long DurationMs,
    string? ErrorCode = null,
    string? ErrorPayloadJson = null,
    string? SpilledFile = null,
    long? FileBytes = null,
    IReadOnlyList<EngineImageReference>? Images = null,
    IReadOnlyList<string>? ValidatedWarnings = null
);

/// <summary>
/// Host-side dispatch port for real tool execution. The engine owns truncation,
/// spill, timeout isolation and contract mapping; the host only runs the tool
/// pipeline and returns the raw outcome. Implementations must never throw:
/// transport faults surface as failed results, cancellation flows through the token.
/// </summary>
public interface IEngineToolBackend
{
    Task<EngineBackendToolResult> InvokeAsync(
        string toolName,
        string argumentsJson,
        EngineToolBackendContext context,
        CancellationToken cancellationToken
    );
}
