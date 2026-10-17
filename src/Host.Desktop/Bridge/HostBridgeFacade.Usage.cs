using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>need12 R4：用量分析 Bridge（只读聚合 + DB 状态，无删除）。C# 名 GetUsageXxx → JS getUsageXxx（CefSharp 默认首字母小驼峰）。</summary>
public sealed partial class HostBridgeFacade
{
    private UsageMetricsService? _usageMetricsOpt;

    public UsageMetricsService UsageMetricsServiceInternal =>
        _usageMetricsOpt ??= new UsageMetricsService(_storageManager);

    private static UsageFilter ParseUsageFilter(string? filterJson)
    {
        var f = new UsageFilter { Preset = "last7" };
        if (string.IsNullOrWhiteSpace(filterJson)) return f;
        try
        {
            using var doc = JsonDocument.Parse(filterJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("preset", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                f.Preset = p.GetString()!.Trim();
            if (root.TryGetProperty("startDate", out var s) && s.ValueKind == JsonValueKind.String)
                f.StartDate = s.GetString();
            if (root.TryGetProperty("endDate", out var e) && e.ValueKind == JsonValueKind.String)
                f.EndDate = e.GetString();
            if (root.TryGetProperty("providerIds", out var pids) && pids.ValueKind == JsonValueKind.Array)
                f.ProviderIds = pids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (root.TryGetProperty("modelKeys", out var mks) && mks.ValueKind == JsonValueKind.Array)
                f.ModelKeys = mks.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }
        catch
        {
            // 解析失败用默认 last7，后续 ResolveRange 仍做严格校验
        }
        return f;
    }

    public Task<BridgeResponse<object>> GetUsageFilterOptions()
    {
        try
        {
            var data = UsageMetricsServiceInternal.GetFilterOptions();
            return Task.FromResult(BridgeResponse<object>.Success(data));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetUsageFilterOptions 失败", ex);
            return Task.FromResult(BridgeResponse<object>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<UsageOverviewDto>> GetUsageOverview(string? filterJson)
    {
        try
        {
            var f = ParseUsageFilter(filterJson);
            var data = UsageMetricsServiceInternal.GetOverview(f);
            return Task.FromResult(BridgeResponse<UsageOverviewDto>.Success(data));
        }
        catch (ArgumentException aex)
        {
            return Task.FromResult(BridgeResponse<UsageOverviewDto>.Fail("USAGE_BAD_FILTER", aex.Message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetUsageOverview 失败", ex);
            return Task.FromResult(BridgeResponse<UsageOverviewDto>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<UsageTrendDto>> GetUsageTrend(string? filterJson, string? metric)
    {
        try
        {
            var f = ParseUsageFilter(filterJson);
            var data = UsageMetricsServiceInternal.GetTrend(f, metric ?? "total");
            return Task.FromResult(BridgeResponse<UsageTrendDto>.Success(data));
        }
        catch (ArgumentException aex)
        {
            return Task.FromResult(BridgeResponse<UsageTrendDto>.Fail("USAGE_BAD_FILTER", aex.Message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetUsageTrend 失败", ex);
            return Task.FromResult(BridgeResponse<UsageTrendDto>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<List<UsageProviderShareItem>>> GetUsageProviderShare(string? filterJson)
    {
        try
        {
            var f = ParseUsageFilter(filterJson);
            var data = UsageMetricsServiceInternal.GetProviderShare(f);
            return Task.FromResult(BridgeResponse<List<UsageProviderShareItem>>.Success(data));
        }
        catch (ArgumentException aex)
        {
            return Task.FromResult(BridgeResponse<List<UsageProviderShareItem>>.Fail("USAGE_BAD_FILTER", aex.Message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetUsageProviderShare 失败", ex);
            return Task.FromResult(BridgeResponse<List<UsageProviderShareItem>>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<object>> QueryUsagePage(string? filterJson, int page, int pageSize)
    {
        try
        {
            var f = ParseUsageFilter(filterJson);
            if (pageSize < 1 || pageSize > 200) pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
            if (page < 1) page = 1;
            var (total, rows) = UsageMetricsServiceInternal.QueryPage(f, page, pageSize);
            long pages = (total + pageSize - 1) / Math.Max(1, pageSize);
            if (total > 0 && page > pages)
                return Task.FromResult(BridgeResponse<object>.Fail("USAGE_PAGE_OVERFLOW", $"页码超界（共 {pages} 页）。"));
            object data = new { total, page, pageSize, rows };
            return Task.FromResult(BridgeResponse<object>.Success(data));
        }
        catch (ArgumentException aex)
        {
            return Task.FromResult(BridgeResponse<object>.Fail("USAGE_BAD_FILTER", aex.Message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] QueryUsagePage 失败", ex);
            return Task.FromResult(BridgeResponse<object>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<object>> GetUsageDbStatus()
    {
        try
        {
            var data = UsageMetricsServiceInternal.GetDbStatus();
            return Task.FromResult(BridgeResponse<object>.Success(data));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetUsageDbStatus 失败", ex);
            return Task.FromResult(BridgeResponse<object>.Fail("BRIDGE_ERROR", ex.Message));
        }
    }
}
