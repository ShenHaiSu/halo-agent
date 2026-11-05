namespace HaloAgent.Engine.Storage.Usage;

/// <summary>用量事件（一行 = 一个 turn 的最终 Usage）。</summary>
public sealed class UsageEvent
{
    public long TsMs { get; set; }
    public string SessionId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderDisplay { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelDisplay { get; set; } = "";
    public string Protocol { get; set; } = "chat";
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    /// <summary>本 turn 内 LLM 成功调用次数（多 round/重试累加；零用量 turn 不落行故不持久化）。</summary>
    public long LlmCalls { get; set; }
    public int Truncated { get; set; }
    public long RecordedMs { get; set; }
}
