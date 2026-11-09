namespace HaloAgent.Engine.Abstractions;

/// <summary>Durable chat message held by the engine ledger.</summary>
public sealed record StoredMessage(
    long Index,
    string Role,
    string Text,
    string TurnId,
    DateTimeOffset StoredAt,
    string Id = "",
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<SessionToolCall>? ToolCalls = null,
    IReadOnlyList<SessionImageReference>? Images = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedTokens = null,
    string? ReasoningContent = null,
    string? ValidatedWarnings = null,
    string? ErrorCode = null,
    DateTimeOffset? CreatedAt = null
);

/// <summary>Assistant-planned tool invocation carried on a history row.</summary>
public sealed record SessionToolCall(string Id, string Name, string Arguments);

/// <summary>Image reference semantics: bytes stay in storage, history keeps the digest.</summary>
public sealed record AttachmentReference(
    string Digest,
    string SessionId,
    string FileName,
    long ByteSize,
    string RelativePath,
    string Mime = "",
    string RelPath = ""
);

/// <summary>Image slot carried on a history row; missing files are flagged at read time, never persisted.</summary>
public sealed record SessionImageReference(
    string ImageId,
    string FileName,
    string Mime,
    string RelPath,
    int Width = 0,
    int Height = 0,
    long SizeBytes = 0,
    bool IsMissing = false
);

/// <summary>Usage counters owned by the engine ledger.</summary>
public sealed record UsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long RetryCount,
    DateTimeOffset UpdatedAt
);

/// <summary>
/// Session descriptor owned by the ledger: workspace isolation, provider
/// selection, title pinning, sub-session lineage and lifecycle timestamps.
/// </summary>
public sealed record SessionDescriptor(
    string SessionId,
    string Title,
    string WorkspaceId,
    string? ParentSessionId = null,
    string? ProviderId = null,
    string? ModelId = null,
    string AgentRole = "main",
    bool IsPinned = false,
    bool TitleLocked = false,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null
);
