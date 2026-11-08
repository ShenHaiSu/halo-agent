namespace HaloAgent.Engine.Abstractions;

/// <summary>Durable chat message held by the engine ledger.</summary>
public sealed record StoredMessage(
    long Index,
    string Role,
    string Text,
    string TurnId,
    DateTimeOffset StoredAt
);

/// <summary>Image reference semantics: bytes stay in storage, history keeps the digest.</summary>
public sealed record AttachmentReference(
    string Digest,
    string SessionId,
    string FileName,
    long ByteSize,
    string RelativePath
);

/// <summary>Usage counters owned by the engine ledger.</summary>
public sealed record UsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long RetryCount,
    DateTimeOffset UpdatedAt
);
