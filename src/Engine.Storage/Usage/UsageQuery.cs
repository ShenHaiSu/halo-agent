namespace HaloAgent.Engine.Storage.Usage;

/// <summary>用量查询参数（query/summary/export 共用，无分页除外）。</summary>
public sealed class UsageQuery
{
    public long? FromMs { get; set; }
    public long? ToMs { get; set; }
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public string? SessionId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    /// <summary>前端时区偏移分钟（new Date().getTimezoneOffset()），byDay 切天用。</summary>
    public int TzOffsetMin { get; set; }
}

/// <summary>按天聚合。</summary>
public sealed class UsageByDay
{
    public string Day { get; set; } = "";
    public long Input { get; set; }
    public long Cached { get; set; }
    public long Output { get; set; }
}

/// <summary>按 (provider,model) 双键聚合。</summary>
public sealed class UsageByModel
{
    public string ProviderId { get; set; } = "";
    public string ProviderDisplay { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelDisplay { get; set; } = "";
    public long Input { get; set; }
    public long Cached { get; set; }
    public long Output { get; set; }
    public long Requests { get; set; }
    public double HitRate { get; set; }
    /// <summary>按 (provider,model) 双键聚合的 LLM 调用次数。</summary>
    public long LlmCalls { get; set; }
}

 /// <summary>汇总结果。</summary>
 public sealed class UsageSummary
 {
     public long Input { get; set; }
     public long Cached { get; set; }
     public long Output { get; set; }
     public long Total { get; set; }
     public long Requests { get; set; }
     public double HitRate { get; set; }
     /// <summary>本筛选范围内落盘 turn 的 LLM 调用次数总和（零用量/中断 turn 不计入）。</summary>
     public long LlmCalls { get; set; }
     public List<UsageByDay> ByDay { get; set; } = new();
     public List<UsageByModel> ByModel { get; set; } = new();
     public bool Degraded { get; set; }
 }
