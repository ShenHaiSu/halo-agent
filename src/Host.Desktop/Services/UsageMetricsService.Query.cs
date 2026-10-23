using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Services;

public sealed partial class UsageMetricsService : IDisposable
{
    private string BuildWhere(UsageFilter f, SqliteCommand cmd, (string Start, string End) range)
    {
        var conds = new List<string>();
        if (range.Start != "0000-00-00")
        {
            var ps = cmd.CreateParameter();
            ps.ParameterName = "$ds";
            ps.Value = range.Start;
            cmd.Parameters.Add(ps);
            var pe = cmd.CreateParameter();
            pe.ParameterName = "$de";
            pe.Value = range.End;
            cmd.Parameters.Add(pe);
            conds.Add("request_date >= $ds AND request_date <= $de");
        }
        ApplyScope(cmd, f, conds);
        return conds.Count == 0 ? "1=1" : string.Join(" AND ", conds);
    }

    private SqliteConnection OpenRead()
    {
        if (!_ready)
            TryInitialize();
        return Open();
    }

    public object GetFilterOptions()
    {
        using var conn = OpenRead();
        var providers = new List<object>();
        var models = new List<object>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText =
                "SELECT DISTINCT provider_id, provider_name FROM llm_usage_records ORDER BY provider_name;";
            using var r = c.ExecuteReader();
            while (r.Read())
                providers.Add(new { id = r.GetString(0), name = r.GetString(1) });
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText =
                "SELECT DISTINCT provider_id, provider_name, model_id, model_display FROM llm_usage_records ORDER BY provider_name, model_id;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                string pid = r.GetString(0),
                    pn = r.GetString(1),
                    mid = r.GetString(2),
                    md = r.IsDBNull(3) ? "" : r.GetString(3);
                models.Add(
                    new
                    {
                        providerId = pid,
                        providerName = pn,
                        modelId = mid,
                        display = string.IsNullOrWhiteSpace(md) ? mid : md,
                        key = pid + "" + mid,
                        label = $"{pn}/{mid}",
                    }
                );
            }
        }
        // need12-P2：来源固定枚举（前端下拉用，不随库空而空）。
        var sources = new List<object>
        {
            new { id = "agent", name = "Agent 对话" },
            new { id = "session", name = "直连会话" },
            new { id = "compact", name = "上下文压缩" },
            new { id = "title", name = "标题生成" },
        };
        return new
        {
            providers,
            models,
            sources
        };
    }

    public UsageOverviewDto GetOverview(UsageFilter f)
    {
        var range = ResolveRange(f);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        string where = BuildWhere(f, cmd, range);
        cmd.CommandText =
            $"SELECT COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cached_tokens),0), COUNT(DISTINCT provider_id || '' || model_id) FROM llm_usage_records WHERE {where};";
        using var r = cmd.ExecuteReader();
        r.Read();
        long n = r.GetInt64(0),
            si = r.GetInt64(1),
            so = r.GetInt64(2),
            sc = r.GetInt64(3);
        int am = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
        return new UsageOverviewDto
        {
            TotalRequests = n,
            SumInput = si,
            SumOutput = so,
            SumCached = sc,
            HitRate = si > 0 ? Math.Clamp((double)sc / si, 0, 1) : 0,
            ActiveModels = am,
        };
    }

    public UsageTrendDto GetTrend(UsageFilter f, string metric)
    {
        var range = ResolveRange(f);
        string m = (metric ?? "total").Trim().ToLowerInvariant();
        if (m != "in" && m != "out" && m != "total")
            m = "total";
        // 日期桶：all 场景按实际 min/max 收敛，避免万天补零
        List<string> dates = new();
        if (range.Start == "0000-00-00")
        {
            using (var conn0 = OpenRead())
            using (var c0 = conn0.CreateCommand())
            {
                c0.CommandText =
                    "SELECT MIN(request_date), MAX(request_date) FROM llm_usage_records;";
                using var r0 = c0.ExecuteReader();
                if (r0.Read() && !r0.IsDBNull(0))
                {
                    DateTime a = DateTime.Parse(r0.GetString(0));
                    DateTime b = DateTime.Parse(r0.GetString(1));
                    if ((b - a).TotalDays > 370)
                        a = b.AddDays(-370);
                    for (var d = a.Date; d <= b.Date; d = d.AddDays(1))
                        dates.Add(d.ToString("yyyy-MM-dd"));
                }
            }
        }
        else
        {
            DateTime a = DateTime.Parse(range.Start),
                b = DateTime.Parse(range.End);
            if ((b - a).TotalDays > 370)
                a = b.AddDays(-370);
            for (var d = a.Date; d <= b.Date; d = d.AddDays(1))
                dates.Add(d.ToString("yyyy-MM-dd"));
        }
        var dto = new UsageTrendDto { Dates = dates };
        var idx = dates
            .Select((x, i) => (x, i))
            .ToDictionary(t => t.x, t => t.i, StringComparer.Ordinal);
        // 系列键
        var seriesMap = new Dictionary<string, UsageTrendSeries>(StringComparer.Ordinal);
        string valExpr =
            m == "in"
                ? "SUM(input_tokens)"
                : m == "out"
                    ? "SUM(output_tokens)"
                    : "SUM(input_tokens+output_tokens)";
        using (var conn = OpenRead())
        {
            using (var cmd = conn.CreateCommand())
            {
                string where = BuildWhere(f, cmd, range);
                // all 且 dates 为空（空库）直接返回空
                cmd.CommandText =
                    $"SELECT request_date, provider_id, provider_name, model_id, {valExpr} FROM llm_usage_records WHERE {where} GROUP BY request_date, provider_id, model_id ORDER BY request_date;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string dd = r.GetString(0),
                        pid = r.GetString(1),
                        pn = r.GetString(2),
                        mid = r.GetString(3);
                    long v = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                    string key = pid + "" + mid;
                    if (!seriesMap.TryGetValue(key, out var s))
                    {
                        s = new UsageTrendSeries
                        {
                            Key = key,
                            ProviderName = pn,
                            ModelId = mid,
                            Values = Enumerable.Repeat(0L, dates.Count).ToList()
                        };
                        seriesMap[key] = s;
                    }
                    if (idx.TryGetValue(dd, out int di))
                        s.Values[di] = v;
                }
            }
            // 每日命中率 + 合计
            using (var cmd2 = conn.CreateCommand())
            {
                string where2 = BuildWhere(f, cmd2, range);
                cmd2.CommandText =
                    $"SELECT request_date, COALESCE(SUM(input_tokens),0), COALESCE(SUM(cached_tokens),0), COALESCE(SUM(input_tokens+output_tokens),0) FROM llm_usage_records WHERE {where2} GROUP BY request_date;";
                var perDay = new Dictionary<string, (long In, long Ca, long Tot)>(
                    StringComparer.Ordinal
                );
                using var r = cmd2.ExecuteReader();
                while (r.Read())
                    perDay[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
                foreach (var dd in dates)
                {
                    if (perDay.TryGetValue(dd, out var t))
                    {
                        dto.HitRates.Add(t.In > 0 ? Math.Clamp((double)t.Ca / t.In, 0, 1) : 0);
                        dto.Totals.Add(t.Tot);
                    }
                    else
                    {
                        dto.HitRates.Add(0);
                        dto.Totals.Add(0);
                    }
                }
            }
        }
        dto.PerModel = seriesMap
            .Values.OrderBy(s => s.ProviderName + "/" + s.ModelId, StringComparer.Ordinal)
            .ToList();
        return dto;
    }

    public List<UsageProviderShareItem> GetProviderShare(UsageFilter f)
    {
        var range = ResolveRange(f);
        var list = new List<UsageProviderShareItem>();
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        string where = BuildWhere(f, cmd, range);
        cmd.CommandText =
            $"SELECT provider_id, provider_name, COALESCE(SUM(input_tokens+output_tokens),0) FROM llm_usage_records WHERE {where} GROUP BY provider_id, provider_name ORDER BY 3 DESC;";
        long total = 0;
        var raw = new List<(string Id, string Name, long V)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                raw.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt64(2)));
                total += raw[^1].V;
            }
        foreach (var (id, name, v) in raw)
            list.Add(
                new UsageProviderShareItem
                {
                    ProviderId = id,
                    Name = name,
                    Value = v,
                    Share = total > 0 ? (double)v / total : 0
                }
            );
        return list;
    }

    public (long Total, List<UsageRowDto> Rows) QueryPage(UsageFilter f, int page, int pageSize)
    {
        if (pageSize < 1 || pageSize > 200)
            pageSize = Math.Clamp(pageSize, 1, 200);
        if (page < 1)
            page = 1;
        var range = ResolveRange(f);
        using var conn = OpenRead();
        long total;
        using (var c = conn.CreateCommand())
        {
            string where = BuildWhere(f, c, range);
            c.CommandText = $"SELECT COUNT(*) FROM llm_usage_records WHERE {where};";
            total = Convert.ToInt64(c.ExecuteScalar());
        }
        var rows = new List<UsageRowDto>();
        using (var c = conn.CreateCommand())
        {
            string where = BuildWhere(f, c, range);
            var po = c.CreateParameter();
            po.ParameterName = "$lim";
            po.Value = pageSize;
            c.Parameters.Add(po);
            var pf = c.CreateParameter();
            pf.ParameterName = "$off";
            pf.Value = (page - 1) * pageSize;
            c.Parameters.Add(pf);
            c.CommandText =
                $"SELECT id, request_date, request_time, request_datetime, provider_name, model_id, model_display, protocol, input_tokens, output_tokens, cached_tokens, hit_rate, status, source, estimated FROM llm_usage_records WHERE {where} ORDER BY request_datetime DESC, id DESC LIMIT $lim OFFSET $off;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                rows.Add(
                    new UsageRowDto
                    {
                        Id = r.GetInt64(0),
                        RequestDate = r.GetString(1),
                        RequestTime = r.GetString(2),
                        RequestDatetime = r.GetString(3),
                        ProviderName = r.GetString(4),
                        ModelId = r.GetString(5),
                        ModelDisplay = r.IsDBNull(6) ? "" : r.GetString(6),
                        Protocol = r.IsDBNull(7) ? "" : r.GetString(7),
                        InputTokens = r.GetInt64(8),
                        OutputTokens = r.GetInt64(9),
                        CachedTokens = r.GetInt64(10),
                        HitRate = r.IsDBNull(11) ? 0 : r.GetDouble(11),
                        Status = r.IsDBNull(12) ? "" : r.GetString(12),
                        Source = r.IsDBNull(13) ? "agent" : r.GetString(13),
                        Estimated = r.IsDBNull(14) ? 0 : r.GetInt32(14),
                    }
                );
            }
        }
        return (total, rows);
    }

    public object GetDbStatus()
    {
        try
        {
            if (!_ready)
                TryInitialize();
            long count = 0;
            string? oldest = null,
                newest = null;
            if (_ready)
            {
                using var conn = Open();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText =
                        "SELECT COUNT(*), MIN(request_date), MAX(request_date) FROM llm_usage_records;";
                    using var r = c.ExecuteReader();
                    if (r.Read())
                    {
                        count = r.GetInt64(0);
                        oldest = r.IsDBNull(1) ? null : r.GetString(1);
                        newest = r.IsDBNull(2) ? null : r.GetString(2);
                    }
                }
            }
            long size = 0;
            try
            {
                if (!string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath))
                    size = new FileInfo(_dbPath).Length;
            }
            catch { }
            return new
            {
                dbPath = _dbPath,
                recordCount = count,
                dbSizeBytes = size,
                oldestDate = oldest,
                newestDate = newest
            };
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetDbStatus 失败", ex);
            return new
            {
                dbPath = _dbPath,
                recordCount = 0L,
                dbSizeBytes = 0L,
                oldestDate = (string?)null,
                newestDate = (string?)null
            };
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _signal.Set();
        }
        catch { }
    }
}
