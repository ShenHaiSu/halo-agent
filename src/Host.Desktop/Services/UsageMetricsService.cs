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

/// <summary>
/// need12 R2：SQLite 用量库（单例、单写者队列、轮末批量写）。
/// DB 路径 {DataRoot}/metrics/usage.db；WAL + busy_timeout；写失败只 Warn 永不回抛。
/// </summary>
public sealed class UsageMetricsService : IDisposable
{
    private readonly StoragePathManager _storage;
    private readonly System.Collections.Concurrent.ConcurrentQueue<List<PendingUsageRow>> _queue = new();
    private readonly Thread _writer;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _disposed;
    private string _dbPath = string.Empty;
    private bool _ready;
    private readonly object _initLock = new();

    /// <summary>测试可注入本地时钟（默认 DateTime.Now）。</summary>
    public Func<DateTime> NowLocal { get; set; } = () => DateTime.Now;

    public UsageMetricsService(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "UsageMetricsWriter" };
        _writer.Start();
        TryInitialize();
    }

    public string DbPath => _dbPath;
    public bool IsReady => _ready;

    public void Reinitialize()
    {
        lock (_initLock) { _ready = false; }
        TryInitialize();
    }

    private void TryInitialize()
    {
        try
        {
            string dir = Path.Combine(_storage.CurrentDataRoot, "metrics");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "usage.db");
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS llm_usage_records (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  provider_id TEXT NOT NULL,
  provider_name TEXT NOT NULL,
  model_id TEXT NOT NULL,
  model_display TEXT NOT NULL DEFAULT '',
  protocol TEXT NOT NULL DEFAULT '',
  input_tokens INTEGER NOT NULL DEFAULT 0,
  output_tokens INTEGER NOT NULL DEFAULT 0,
  cached_tokens INTEGER NOT NULL DEFAULT 0,
  hit_rate REAL NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'ok',
  source TEXT NOT NULL DEFAULT 'agent',
  estimated INTEGER NOT NULL DEFAULT 0,
  request_id TEXT NOT NULL DEFAULT '',
  session_id TEXT NOT NULL DEFAULT '',
  workspace_id TEXT NOT NULL DEFAULT '',
  request_date TEXT NOT NULL,
  request_time TEXT NOT NULL,
  request_datetime TEXT NOT NULL,
  created_utc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_llm_usage_request ON llm_usage_records(request_id);
CREATE INDEX IF NOT EXISTS ix_llm_usage_dt ON llm_usage_records(request_datetime);
CREATE INDEX IF NOT EXISTS ix_llm_usage_date ON llm_usage_records(request_date);
CREATE INDEX IF NOT EXISTS ix_llm_usage_pm ON llm_usage_records(provider_id, model_id);";
            cmd.ExecuteNonQuery();
            // need12-P2：老库平滑迁移（列缺失才加；失败只 Warn 不影响对话）
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE llm_usage_records ADD COLUMN source TEXT NOT NULL DEFAULT 'agent';";
                mig.ExecuteNonQuery();
            }
            catch { /* 列已存在则忽略 */ }
            try
            {
                using var mig2 = conn.CreateCommand();
                mig2.CommandText = "ALTER TABLE llm_usage_records ADD COLUMN estimated INTEGER NOT NULL DEFAULT 0;";
                mig2.ExecuteNonQuery();
            }
            catch { /* 列已存在则忽略 */ }
            try
            {
                using var idx = conn.CreateCommand();
                idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_llm_usage_source ON llm_usage_records(source);";
                idx.ExecuteNonQuery();
            }
            catch { /* 索引失败忽略 */ }
            using var prag = conn.CreateCommand();
            prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            prag.ExecuteNonQuery();
            lock (_initLock) { _ready = true; }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] 用量库初始化失败，不影响对话", ex);
            lock (_initLock) { _ready = false; }
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>轮末入队（立即返回，永不抛）。空集合直接忽略。</summary>
    public void EnqueueFlush(List<PendingUsageRow> rows, string sessionId, string workspaceId)
    {
        try
        {
            if (rows == null || rows.Count == 0) return;
            if (!_ready) TryInitialize();
            // 打快照：隔离调用方后续修改
            var snapshot = rows.Select(r => new PendingUsageRow
            {
                ProviderId = r.ProviderId ?? string.Empty,
                ProviderName = r.ProviderName ?? string.Empty,
                ModelId = r.ModelId ?? string.Empty,
                ModelDisplay = r.ModelDisplay ?? r.ModelId ?? string.Empty,
                Protocol = r.Protocol ?? string.Empty,
                Input = Math.Max(0, r.Input),
                Output = Math.Max(0, r.Output),
                Cached = Math.Max(0, r.Cached),
                Status = string.IsNullOrWhiteSpace(r.Status) ? "ok" : r.Status,
                RequestId = string.IsNullOrWhiteSpace(r.RequestId) ? Guid.NewGuid().ToString("N") : r.RequestId,
                SessionId = string.IsNullOrWhiteSpace(r.SessionId) ? (sessionId ?? string.Empty) : r.SessionId,
                WorkspaceId = string.IsNullOrWhiteSpace(r.WorkspaceId) ? (workspaceId ?? string.Empty) : r.WorkspaceId,
                Source = UsageRecorder.NormalizeSource(r.Source, UsageRecorder.SourceAgent),
                Estimated = r.Estimated != 0 ? 1 : 0,
            }).ToList();
            _queue.Enqueue(snapshot);
            _signal.Set();
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] EnqueueFlush 失败，已丢弃本批", ex);
        }
    }

    private void WriterLoop()
    {
        while (!_disposed)
        {
            try { _signal.WaitOne(1000); } catch { /* ignore */ }
            if (_disposed) break;
            while (_queue.TryDequeue(out var batch))
            {
                try { FlushBatch(batch); }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn("[Usage] 批量写库失败，已丢弃本批", ex);
                }
            }
        }
    }

    private void FlushBatch(List<PendingUsageRow> batch)
    {
        if (batch == null || batch.Count == 0) return;
        if (!_ready) TryInitialize();
        if (!_ready) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime now = NowLocal();
        string d = now.ToString("yyyy-MM-dd");
        string t = now.ToString("HH:mm:ss");
        string dt = now.ToString("yyyy-MM-dd HH:mm:ss");
        string utc = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR IGNORE INTO llm_usage_records
(provider_id,provider_name,model_id,model_display,protocol,input_tokens,output_tokens,cached_tokens,hit_rate,status,source,estimated,request_id,session_id,workspace_id,request_date,request_time,request_datetime,created_utc)
VALUES ($p,$pn,$m,$md,$pr,$i,$o,$c,$h,$s,$src,$est,$r,$sess,$ws,$d,$t,$dt,$u);";
        var pp = cmd.CreateParameter(); pp.ParameterName = "$p"; cmd.Parameters.Add(pp);
        var ppn = cmd.CreateParameter(); ppn.ParameterName = "$pn"; cmd.Parameters.Add(ppn);
        var pm = cmd.CreateParameter(); pm.ParameterName = "$m"; cmd.Parameters.Add(pm);
        var pmd = cmd.CreateParameter(); pmd.ParameterName = "$md"; cmd.Parameters.Add(pmd);
        var ppr = cmd.CreateParameter(); ppr.ParameterName = "$pr"; cmd.Parameters.Add(ppr);
        var pi = cmd.CreateParameter(); pi.ParameterName = "$i"; cmd.Parameters.Add(pi);
        var po = cmd.CreateParameter(); po.ParameterName = "$o"; cmd.Parameters.Add(po);
        var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
        var ph = cmd.CreateParameter(); ph.ParameterName = "$h"; cmd.Parameters.Add(ph);
        var ps = cmd.CreateParameter(); ps.ParameterName = "$s"; cmd.Parameters.Add(ps);
        var psrc = cmd.CreateParameter(); psrc.ParameterName = "$src"; cmd.Parameters.Add(psrc);
        var pest = cmd.CreateParameter(); pest.ParameterName = "$est"; cmd.Parameters.Add(pest);
        var pr = cmd.CreateParameter(); pr.ParameterName = "$r"; cmd.Parameters.Add(pr);
        var pse = cmd.CreateParameter(); pse.ParameterName = "$sess"; cmd.Parameters.Add(pse);
        var pws = cmd.CreateParameter(); pws.ParameterName = "$ws"; cmd.Parameters.Add(pws);
        var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
        var pt = cmd.CreateParameter(); pt.ParameterName = "$t"; cmd.Parameters.Add(pt);
        var pdt = cmd.CreateParameter(); pdt.ParameterName = "$dt"; cmd.Parameters.Add(pdt);
        var pu = cmd.CreateParameter(); pu.ParameterName = "$u"; cmd.Parameters.Add(pu);
        foreach (var r in batch)
        {
            double hit = r.Input > 0 ? Math.Clamp((double)r.Cached / r.Input, 0, 1) : 0;
            pp.Value = r.ProviderId; ppn.Value = r.ProviderName; pm.Value = r.ModelId;
            pmd.Value = r.ModelDisplay; ppr.Value = r.Protocol;
            pi.Value = r.Input; po.Value = r.Output; pc.Value = r.Cached; ph.Value = hit;
            ps.Value = r.Status; psrc.Value = UsageRecorder.NormalizeSource(r.Source, UsageRecorder.SourceAgent); pest.Value = r.Estimated != 0 ? 1 : 0;
            pr.Value = r.RequestId; pse.Value = r.SessionId; pws.Value = r.WorkspaceId;
            pd.Value = d; pt.Value = t; pdt.Value = dt; pu.Value = utc;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        sw.Stop();
        HaloLogger.App.Info($"[Usage] 轮末批量写库 {batch.Count} 行，flushMs={sw.ElapsedMilliseconds}");
    }

    // ---------- 查询侧（全部服务端聚合） ----------

    public (string Start, string End) ResolveRange(UsageFilter f)
    {
        string preset = (f?.Preset ?? "last7").Trim();
        DateTime now = NowLocal().Date;
        string fmt(DateTime x) => x.ToString("yyyy-MM-dd");
        switch (preset)
        {
            case "today": return (fmt(now), fmt(now));
            case "yesterday": return (fmt(now.AddDays(-1)), fmt(now.AddDays(-1)));
            case "last3": return (fmt(now.AddDays(-2)), fmt(now));
            case "last7": return (fmt(now.AddDays(-6)), fmt(now));
            case "last30": return (fmt(now.AddDays(-29)), fmt(now));
            case "thisWeek":
                {
                    // 周一起算：周日(0)→-6，其余 -(dow-1)
                    int dow = (int)now.DayOfWeek;
                    int back = dow == 0 ? 6 : dow - 1;
                    return (fmt(now.AddDays(-back)), fmt(now));
                }
            case "thisMonth": return (fmt(new DateTime(now.Year, now.Month, 1)), fmt(now));
            case "all": return ("0000-00-00", "9999-99-99");
            case "custom":
                {
                    string s = (f?.StartDate ?? "").Trim();
                    string e = (f?.EndDate ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(e))
                        throw new ArgumentException("自定义日期范围须同时提供 startDate/endDate（yyyy-MM-dd）。");
                    if (string.Compare(s, e, StringComparison.Ordinal) > 0)
                        throw new ArgumentException("startDate 不得晚于 endDate。");
                    return (s, e);
                }
            default: throw new ArgumentException($"未知 preset：'{preset}'。");
        }
    }

    private static void ApplyScope(SqliteCommand cmd, UsageFilter f, List<string> conds)
    {
        var pids = (f?.ProviderIds ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (pids.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < pids.Count; i++)
            {
                string n = "$pid" + i;
                names.Add(n);
                var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = pids[i]; cmd.Parameters.Add(p);
            }
            conds.Add($"provider_id IN ({string.Join(",", names)})");
        }
        var mks = (f?.ModelKeys ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (mks.Count > 0)
        {
            var orParts = new List<string>();
            for (int i = 0; i < mks.Count; i++)
            {
                string[] sp = mks[i].Split('\x1f');
                if (sp.Length != 2) continue;
                string a = "$mkp" + i, b = "$mkm" + i;
                var pa = cmd.CreateParameter(); pa.ParameterName = a; pa.Value = sp[0]; cmd.Parameters.Add(pa);
                var pb = cmd.CreateParameter(); pb.ParameterName = b; pb.Value = sp[1]; cmd.Parameters.Add(pb);
                orParts.Add($"(provider_id={a} AND model_id={b})");
            }
            if (orParts.Count > 0) conds.Add("(" + string.Join(" OR ", orParts) + ")");
        }
        // need12-P2：来源筛选（非法值忽略，空=全部）；估算筛选（null=全部）。
        var srcs = (f?.Sources ?? new List<string>()).Where(x => UsageRecorder.IsValidSource(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        if (srcs.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < srcs.Count; i++)
            {
                string n = "$src" + i;
                names.Add(n);
                var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = srcs[i]; cmd.Parameters.Add(p);
            }
            conds.Add($"source IN ({string.Join(",", names)})");
        }
        if (f?.EstimatedOnly == true) conds.Add("estimated = 1");
        else if (f?.EstimatedOnly == false) conds.Add("estimated = 0");
    }

    private string BuildWhere(UsageFilter f, SqliteCommand cmd, (string Start, string End) range)
    {
        var conds = new List<string>();
        if (range.Start != "0000-00-00")
        {
            var ps = cmd.CreateParameter(); ps.ParameterName = "$ds"; ps.Value = range.Start; cmd.Parameters.Add(ps);
            var pe = cmd.CreateParameter(); pe.ParameterName = "$de"; pe.Value = range.End; cmd.Parameters.Add(pe);
            conds.Add("request_date >= $ds AND request_date <= $de");
        }
        ApplyScope(cmd, f, conds);
        return conds.Count == 0 ? "1=1" : string.Join(" AND ", conds);
    }

    private SqliteConnection OpenRead()
    {
        if (!_ready) TryInitialize();
        return Open();
    }

    public object GetFilterOptions()
    {
        using var conn = OpenRead();
        var providers = new List<object>();
        var models = new List<object>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT DISTINCT provider_id, provider_name FROM llm_usage_records ORDER BY provider_name;";
            using var r = c.ExecuteReader();
            while (r.Read()) providers.Add(new { id = r.GetString(0), name = r.GetString(1) });
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT DISTINCT provider_id, provider_name, model_id, model_display FROM llm_usage_records ORDER BY provider_name, model_id;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                string pid = r.GetString(0), pn = r.GetString(1), mid = r.GetString(2), md = r.IsDBNull(3) ? "" : r.GetString(3);
                models.Add(new
                {
                    providerId = pid,
                    providerName = pn,
                    modelId = mid,
                    display = string.IsNullOrWhiteSpace(md) ? mid : md,
                    key = pid + "" + mid,
                    label = $"{pn}/{mid}",
                });
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
        return new { providers, models, sources };
    }

    public UsageOverviewDto GetOverview(UsageFilter f)
    {
        var range = ResolveRange(f);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        string where = BuildWhere(f, cmd, range);
        cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cached_tokens),0), COUNT(DISTINCT provider_id || '' || model_id) FROM llm_usage_records WHERE {where};";
        using var r = cmd.ExecuteReader();
        r.Read();
        long n = r.GetInt64(0), si = r.GetInt64(1), so = r.GetInt64(2), sc = r.GetInt64(3);
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
        if (m != "in" && m != "out" && m != "total") m = "total";
        // 日期桶：all 场景按实际 min/max 收敛，避免万天补零
        List<string> dates = new();
        if (range.Start == "0000-00-00")
        {
            using (var conn0 = OpenRead())
            using (var c0 = conn0.CreateCommand())
            {
                c0.CommandText = "SELECT MIN(request_date), MAX(request_date) FROM llm_usage_records;";
                using var r0 = c0.ExecuteReader();
                if (r0.Read() && !r0.IsDBNull(0))
                {
                    DateTime a = DateTime.Parse(r0.GetString(0));
                    DateTime b = DateTime.Parse(r0.GetString(1));
                    if ((b - a).TotalDays > 370) a = b.AddDays(-370);
                    for (var d = a.Date; d <= b.Date; d = d.AddDays(1)) dates.Add(d.ToString("yyyy-MM-dd"));
                }
            }
        }
        else
        {
            DateTime a = DateTime.Parse(range.Start), b = DateTime.Parse(range.End);
            if ((b - a).TotalDays > 370) a = b.AddDays(-370);
            for (var d = a.Date; d <= b.Date; d = d.AddDays(1)) dates.Add(d.ToString("yyyy-MM-dd"));
        }
        var dto = new UsageTrendDto { Dates = dates };
        var idx = dates.Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i, StringComparer.Ordinal);
        // 系列键
        var seriesMap = new Dictionary<string, UsageTrendSeries>(StringComparer.Ordinal);
        string valExpr = m == "in" ? "SUM(input_tokens)" : m == "out" ? "SUM(output_tokens)" : "SUM(input_tokens+output_tokens)";
        using (var conn = OpenRead())
        {
             using (var cmd = conn.CreateCommand())
             {
                 string where = BuildWhere(f, cmd, range);
                // all 且 dates 为空（空库）直接返回空
                cmd.CommandText = $"SELECT request_date, provider_id, provider_name, model_id, {valExpr} FROM llm_usage_records WHERE {where} GROUP BY request_date, provider_id, model_id ORDER BY request_date;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string dd = r.GetString(0), pid = r.GetString(1), pn = r.GetString(2), mid = r.GetString(3);
                    long v = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                    string key = pid + "" + mid;
                    if (!seriesMap.TryGetValue(key, out var s))
                    {
                        s = new UsageTrendSeries { Key = key, ProviderName = pn, ModelId = mid, Values = Enumerable.Repeat(0L, dates.Count).ToList() };
                        seriesMap[key] = s;
                    }
                    if (idx.TryGetValue(dd, out int di)) s.Values[di] = v;
                }
            }
             // 每日命中率 + 合计
             using (var cmd2 = conn.CreateCommand())
             {
                 string where2 = BuildWhere(f, cmd2, range);
                 cmd2.CommandText = $"SELECT request_date, COALESCE(SUM(input_tokens),0), COALESCE(SUM(cached_tokens),0), COALESCE(SUM(input_tokens+output_tokens),0) FROM llm_usage_records WHERE {where2} GROUP BY request_date;";
                 var perDay = new Dictionary<string, (long In, long Ca, long Tot)>(StringComparer.Ordinal);
                 using var r = cmd2.ExecuteReader();
                 while (r.Read()) perDay[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
                 foreach (var dd in dates)
                 {
                     if (perDay.TryGetValue(dd, out var t))
                     {
                         dto.HitRates.Add(t.In > 0 ? Math.Clamp((double)t.Ca / t.In, 0, 1) : 0);
                         dto.Totals.Add(t.Tot);
                     }
                     else { dto.HitRates.Add(0); dto.Totals.Add(0); }
                 }
             }
        }
        dto.PerModel = seriesMap.Values.OrderBy(s => s.ProviderName + "/" + s.ModelId, StringComparer.Ordinal).ToList();
        return dto;
    }

    public List<UsageProviderShareItem> GetProviderShare(UsageFilter f)
    {
        var range = ResolveRange(f);
        var list = new List<UsageProviderShareItem>();
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        string where = BuildWhere(f, cmd, range);
        cmd.CommandText = $"SELECT provider_id, provider_name, COALESCE(SUM(input_tokens+output_tokens),0) FROM llm_usage_records WHERE {where} GROUP BY provider_id, provider_name ORDER BY 3 DESC;";
        long total = 0;
        var raw = new List<(string Id, string Name, long V)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { raw.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt64(2))); total += raw[^1].V; }
        foreach (var (id, name, v) in raw)
            list.Add(new UsageProviderShareItem { ProviderId = id, Name = name, Value = v, Share = total > 0 ? (double)v / total : 0 });
        return list;
    }

    public (long Total, List<UsageRowDto> Rows) QueryPage(UsageFilter f, int page, int pageSize)
    {
        if (pageSize < 1 || pageSize > 200) pageSize = Math.Clamp(pageSize, 1, 200);
        if (page < 1) page = 1;
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
            var po = c.CreateParameter(); po.ParameterName = "$lim"; po.Value = pageSize; c.Parameters.Add(po);
            var pf = c.CreateParameter(); pf.ParameterName = "$off"; pf.Value = (page - 1) * pageSize; c.Parameters.Add(pf);
            c.CommandText = $"SELECT id, request_date, request_time, request_datetime, provider_name, model_id, model_display, protocol, input_tokens, output_tokens, cached_tokens, hit_rate, status, source, estimated FROM llm_usage_records WHERE {where} ORDER BY request_datetime DESC, id DESC LIMIT $lim OFFSET $off;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new UsageRowDto
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
                });
            }
        }
        return (total, rows);
    }

    public object GetDbStatus()
    {
        try
        {
            if (!_ready) TryInitialize();
            long count = 0;
            string? oldest = null, newest = null;
            if (_ready)
            {
                using var conn = Open();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT COUNT(*), MIN(request_date), MAX(request_date) FROM llm_usage_records;";
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
            try { if (!string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath)) size = new FileInfo(_dbPath).Length; } catch { }
            return new { dbPath = _dbPath, recordCount = count, dbSizeBytes = size, oldestDate = oldest, newestDate = newest };
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] GetDbStatus 失败", ex);
            return new { dbPath = _dbPath, recordCount = 0L, dbSizeBytes = 0L, oldestDate = (string?)null, newestDate = (string?)null };
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _signal.Set(); } catch { }
    }
}
