namespace Host.Desktop.Core.Models;

/// <summary>P007：消息语义 kind（DB messages.kind / UnifiedChatMessage.Kind）。NULL / 'chat' 视为普通消息。</summary>
public static class MessageKind
{
    public const string Chat = "chat";
    public const string CompactStub = "compact_stub";
}

/// <summary>P007：轮次语义 kind（DB turns.kind）。</summary>
public static class TurnKind
{
    public const string Turn = "turn";
    public const string Compact = "compact";
}

/// <summary>P007：压缩模型开关：legacy（旧重写）/ stub（新桩模型，默认关闭）。</summary>
public static class CompactMode
{
    public const string Legacy = "legacy";
    public const string Stub = "stub";
}

/// <summary>P007：压缩桩的结构化元数据（messages.meta_json，version=1）。</summary>
public sealed class CompactStubMeta
{
    public int V { get; set; } = 1;
    public int RemovedCount { get; set; }

    /// <summary>DB 路径可得（messages.seq）；纯 jsonl 会话为 null。</summary>
    public long? FromSeq { get; set; }
    public long? ToSeq { get; set; }
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public int SummaryTokens { get; set; }
    public int PromptTokens { get; set; }
    public int CachedTokens { get; set; }
    public string? BackupFile { get; set; }
}

/// <summary>P007：读取桩（活跃切片起点）用的最小视图。</summary>
public sealed class CompactStubInfo
{
    public string MessageId { get; init; } = string.Empty;
    public string TurnId { get; init; } = string.Empty;
    public long? Seq { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int SummaryTokens { get; init; }
    public int PromptTokens { get; init; }
    public int CachedTokens { get; init; }
    public int RemovedCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
