using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Services;

/// <summary>need12：轮内待落库用量行（纯内存，轮末统一 EnqueueFlush）。时间三列由 Service 按本地时钟统一生成。</summary>
public sealed class PendingUsageRow
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelDisplay { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public long Input { get; set; }
    public long Output { get; set; }
    public long Cached { get; set; }
    public string Status { get; set; } = "ok";
    public string RequestId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>need12-P2：请求来源 agent|session|compact|title（老库回填 agent）。</summary>
    public string Source { get; set; } = "agent";

    /// <summary>need12-P2：1=本地估算（无网关实测），0=网关实测。</summary>
    public int Estimated { get; set; }
}

/// <summary>need12：用量过滤器（前后端同构，preset 由后端换算为闭区间）。</summary>
public sealed class UsageFilter
{
    public string Preset { get; set; } = "last7";
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public List<string>? ProviderIds { get; set; }
    public List<string>? ModelKeys { get; set; }

    /// <summary>need12-P2：来源筛选 agent|session|compact|title（空=全部）。</summary>
    public List<string>? Sources { get; set; }

    /// <summary>need12-P2：是否只看估算行（null=全部，true=仅估算，false=仅实测）。</summary>
    public bool? EstimatedOnly { get; set; }
}

public sealed class UsageOverviewDto
{
    public long TotalRequests { get; set; }
    public long SumInput { get; set; }
    public long SumOutput { get; set; }
    public long SumCached { get; set; }
    public double HitRate { get; set; }
    public int ActiveModels { get; set; }
}

public sealed class UsageTrendDto
{
    public List<string> Dates { get; set; } = new();
    public List<UsageTrendSeries> PerModel { get; set; } = new();
    public List<double> HitRates { get; set; } = new();
    public List<long> Totals { get; set; } = new();
}

public sealed class UsageTrendSeries
{
    public string Key { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public List<long> Values { get; set; } = new();
}

public sealed class UsageProviderShareItem
{
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
    public double Share { get; set; }
}

public sealed class UsageRowDto
{
    public long Id { get; set; }
    public string RequestDate { get; set; } = string.Empty;
    public string RequestTime { get; set; } = string.Empty;
    public string RequestDatetime { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelDisplay { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public double HitRate { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>need12-P2：请求来源 agent|session|compact|title。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>need12-P2：1=本地估算，0=网关实测。</summary>
    public int Estimated { get; set; }
}
