using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Storage.Usage;

namespace HaloAgent.Engine.Server.Routing;

/// <summary>P16 用量 RPC：usage.query / summary / stats / export / clear。</summary>
public sealed partial class RealMethods
{
    private UsageQuery ParseUsageQuery(JsonElement? p, bool paged)
    {
        var q = new UsageQuery();
        if (p is null) return q;
        var root = p.Value;
        if (root.ValueKind != JsonValueKind.Object) return q;

        if (root.TryGetProperty("fromMs", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var fv)) q.FromMs = fv;
        if (root.TryGetProperty("toMs", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tv)) q.ToMs = tv;
        if (q.FromMs.HasValue && q.ToMs.HasValue && q.FromMs.Value > q.ToMs.Value)
            throw RpcException.ParamsInvalid("fromMs 不能大于 toMs");
        if (root.TryGetProperty("providerId", out var pid) && pid.ValueKind == JsonValueKind.String)
        {
            var s = pid.GetString();
            if (!string.IsNullOrEmpty(s)) q.ProviderId = s;
        }
        if (root.TryGetProperty("modelId", out var mid) && mid.ValueKind == JsonValueKind.String)
        {
            var s = mid.GetString();
            if (!string.IsNullOrEmpty(s)) q.ModelId = s;
        }
        if (root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
        {
            var s = sid.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s)) q.SessionId = s;
        }
        if (root.TryGetProperty("tzOffsetMin", out var tz) && tz.ValueKind == JsonValueKind.Number && tz.TryGetInt32(out var tzv)) q.TzOffsetMin = tzv;
        if (root.TryGetProperty("order", out var ord) && ord.ValueKind == JsonValueKind.String)
        {
            var s = ord.GetString();
            if (string.Equals(s, "asc", StringComparison.OrdinalIgnoreCase))
                throw RpcException.ParamsInvalid("order 恒为 desc，不接受 asc");
        }
        if (paged)
        {
            if (root.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.Number && pg.TryGetInt32(out var pgv)) q.Page = Math.Max(1, pgv);
            if (root.TryGetProperty("pageSize", out var ps) && ps.ValueKind == JsonValueKind.Number && ps.TryGetInt32(out var psv)) q.PageSize = Math.Clamp(psv <= 0 ? 50 : psv, 1, 200);
        }
        return q;
    }

    private static double HitRateOf(long prompt, long cached) =>
        prompt > 0 ? (double)cached / prompt : 0;

    public Task<object?> UsageQueryAsync(JsonElement? p, CancellationToken ct)
    {
        var q = ParseUsageQuery(p, paged: true);
        _usageStore.EnsureCreated();
        var (items, total) = _usageStore.Query(q);
        var rows = items.Select(e => new
        {
            tsMs = e.TsMs,
            sessionId = e.SessionId,
            turnId = e.TurnId,
            providerId = e.ProviderId,
            providerDisplay = e.ProviderDisplay,
            modelId = e.ModelId,
            modelDisplay = e.ModelDisplay,
            protocol = e.Protocol,
            prompt = e.PromptTokens,
            cached = e.CachedTokens,
            output = e.CompletionTokens,
            total = e.TotalTokens,
            hitRate = HitRateOf(e.PromptTokens, e.CachedTokens),
            truncated = e.Truncated,
            degraded = _usageStore.Degraded,
        }).ToList();
        return Task.FromResult<object?>(new { items = rows, total, degraded = _usageStore.Degraded });
    }

    public Task<object?> UsageSummaryAsync(JsonElement? p, CancellationToken ct)
    {
        var q = ParseUsageQuery(p, paged: false);
        _usageStore.EnsureCreated();
        var s = _usageStore.Summary(q);
        return Task.FromResult<object?>(new
        {
            input = s.Input,
            cached = s.Cached,
            output = s.Output,
            total = s.Total,
            requests = s.Requests,
            hitRate = s.HitRate,
            byDay = s.ByDay.Select(d => new { day = d.Day, input = d.Input, cached = d.Cached, output = d.Output }).ToList(),
            byModel = s.ByModel.Select(m => new
            {
                providerId = m.ProviderId,
                providerDisplay = m.ProviderDisplay,
                modelId = m.ModelId,
                modelDisplay = m.ModelDisplay,
                input = m.Input,
                cached = m.Cached,
                output = m.Output,
                requests = m.Requests,
                hitRate = m.HitRate,
            }).ToList(),
            degraded = _usageStore.Degraded,
        });
    }

    public Task<object?> UsageStatsAsync(JsonElement? p, CancellationToken ct)
    {
        _usageStore.EnsureCreated();
        var (size, rows) = _usageStore.Stats();
        return Task.FromResult<object?>(new { sizeBytes = size, rowCount = rows, degraded = _usageStore.Degraded });
    }

     public Task<object?> UsageExportAsync(JsonElement? p, CancellationToken ct)
     {
         var q = ParseUsageQuery(p, paged: false);
         _usageStore.EnsureCreated();
         var (csv, truncated, _) = _usageStore.ExportCsv(q);
         return Task.FromResult<object?>(new { csv, truncated, degraded = _usageStore.Degraded });
     }
 
     /// <summary>P17 导出落盘：选路→引擎写盘（.tmp→rename）→reveal。旧 usage.export 保留给浏览器/Mock，桌面禁用 Blob 分支。</summary>
     public Task<object?> UsageExportFileAsync(JsonElement? p, CancellationToken ct)
     {
         if (p is null || p.Value.ValueKind != JsonValueKind.Object)
             throw RpcException.ParamsInvalid("path 必需（绝对路径 .csv）");
         var q = ParseUsageQuery(p, paged: false);
         if (!p.Value.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String
             || string.IsNullOrWhiteSpace(pathEl.GetString()))
             throw RpcException.ParamsInvalid("path 必需（绝对路径 .csv）");
         var path = pathEl.GetString()!;
         if (!Path.IsPathFullyQualified(path)
             || !string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
             throw RpcException.ParamsInvalid("path 须为绝对路径且以 .csv 结尾");
         IReadOnlyDictionary<string, string>? maskMap = null;
         if (p.Value.TryGetProperty("maskMap", out var mm) && mm.ValueKind == JsonValueKind.Object)
         {
             var dict = new Dictionary<string, string>(StringComparer.Ordinal);
             foreach (var prop in mm.EnumerateObject())
             {
                 if (prop.Value.ValueKind == JsonValueKind.String)
                     dict[prop.Name] = prop.Value.GetString() ?? "";
             }
             maskMap = dict;
         }
         _usageStore.EnsureCreated();
         var (wrote, truncated) = _usageStore.ExportFile(q, path, maskMap);
         return Task.FromResult<object?>(new { path, wrote, truncated, degraded = _usageStore.Degraded });
     }

    public Task<object?> UsageClearAsync(JsonElement? p, CancellationToken ct)
    {
        if (p is null || p.Value.ValueKind != JsonValueKind.Object
            || !p.Value.TryGetProperty("beforeMs", out var b)
            || b.ValueKind != JsonValueKind.Number || !b.TryGetInt64(out var beforeMs))
            throw RpcException.ParamsInvalid("beforeMs 必需（整数毫秒）");
        if (beforeMs >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw RpcException.ParamsInvalid("beforeMs 必须为过去时间");
        _usageStore.EnsureCreated();
        var removed = _usageStore.ClearBefore(beforeMs);
        return Task.FromResult<object?>(new { removed, degraded = _usageStore.Degraded });
    }
}
