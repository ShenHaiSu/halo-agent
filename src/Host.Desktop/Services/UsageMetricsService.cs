using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Services;

/// <summary>
/// need12 R2：SQLite 用量库（单例、单写者队列、轮末批量写）。
/// DB 路径 {DataRoot}/metrics/usage.db；WAL + busy_timeout；写失败只 Warn 永不回抛。
/// </summary>
public sealed partial class UsageMetricsService : IDisposable
{
    private readonly StoragePathManager _storage;
    private readonly System.Collections.Concurrent.ConcurrentQueue<List<PendingUsageRow>> _queue =
        new();
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
        lock (_initLock)
        {
            _ready = false;
        }
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
            cmd.CommandText =
                @"
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
                mig.CommandText =
                    "ALTER TABLE llm_usage_records ADD COLUMN source TEXT NOT NULL DEFAULT 'agent';";
                mig.ExecuteNonQuery();
            }
            catch
            { /* 列已存在则忽略 */
            }
            try
            {
                using var mig2 = conn.CreateCommand();
                mig2.CommandText =
                    "ALTER TABLE llm_usage_records ADD COLUMN estimated INTEGER NOT NULL DEFAULT 0;";
                mig2.ExecuteNonQuery();
            }
            catch
            { /* 列已存在则忽略 */
            }
            try
            {
                using var idx = conn.CreateCommand();
                idx.CommandText =
                    "CREATE INDEX IF NOT EXISTS ix_llm_usage_source ON llm_usage_records(source);";
                idx.ExecuteNonQuery();
            }
            catch
            { /* 索引失败忽略 */
            }
            using var prag = conn.CreateCommand();
            prag.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            prag.ExecuteNonQuery();
            lock (_initLock)
            {
                _ready = true;
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Usage] 用量库初始化失败，不影响对话", ex);
            lock (_initLock)
            {
                _ready = false;
            }
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString()
        );
        conn.Open();
        return conn;
    }

    /// <summary>轮末入队（立即返回，永不抛）。空集合直接忽略。</summary>
    public void EnqueueFlush(List<PendingUsageRow> rows, string sessionId, string workspaceId)
    {
        try
        {
            if (rows == null || rows.Count == 0)
                return;
            if (!_ready)
                TryInitialize();
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
                    RequestId = string.IsNullOrWhiteSpace(r.RequestId)
                        ? Guid.NewGuid().ToString("N")
                        : r.RequestId,
                    SessionId = string.IsNullOrWhiteSpace(r.SessionId)
                        ? (sessionId ?? string.Empty)
                        : r.SessionId,
                    WorkspaceId = string.IsNullOrWhiteSpace(r.WorkspaceId)
                        ? (workspaceId ?? string.Empty)
                        : r.WorkspaceId,
                    Source = UsageRecorder.NormalizeSource(r.Source, UsageRecorder.SourceAgent),
                    Estimated = r.Estimated != 0 ? 1 : 0,
                })
                .ToList();
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
            try
            {
                _signal.WaitOne(1000);
            }
            catch
            { /* ignore */
            }
            if (_disposed)
                break;
            while (_queue.TryDequeue(out var batch))
            {
                try
                {
                    FlushBatch(batch);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn("[Usage] 批量写库失败，已丢弃本批", ex);
                }
            }
        }
    }

    private void FlushBatch(List<PendingUsageRow> batch)
    {
        if (batch == null || batch.Count == 0)
            return;
        if (!_ready)
            TryInitialize();
        if (!_ready)
            return;
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
        cmd.CommandText =
            @"INSERT INTO llm_usage_records
(provider_id,provider_name,model_id,model_display,protocol,input_tokens,output_tokens,cached_tokens,hit_rate,status,source,estimated,request_id,session_id,workspace_id,request_date,request_time,request_datetime,created_utc)
VALUES ($p,$pn,$m,$md,$pr,$i,$o,$c,$h,$s,$src,$est,$r,$sess,$ws,$d,$t,$dt,$u)
ON CONFLICT(request_id) DO UPDATE SET
input_tokens=llm_usage_records.input_tokens+excluded.input_tokens,
output_tokens=llm_usage_records.output_tokens+excluded.output_tokens,
cached_tokens=llm_usage_records.cached_tokens+excluded.cached_tokens,
hit_rate=CASE WHEN (llm_usage_records.input_tokens+excluded.input_tokens)>0 THEN CAST((llm_usage_records.cached_tokens+excluded.cached_tokens) AS REAL)/(llm_usage_records.input_tokens+excluded.input_tokens) ELSE 0 END;";
        var pp = cmd.CreateParameter();
        pp.ParameterName = "$p";
        cmd.Parameters.Add(pp);
        var ppn = cmd.CreateParameter();
        ppn.ParameterName = "$pn";
        cmd.Parameters.Add(ppn);
        var pm = cmd.CreateParameter();
        pm.ParameterName = "$m";
        cmd.Parameters.Add(pm);
        var pmd = cmd.CreateParameter();
        pmd.ParameterName = "$md";
        cmd.Parameters.Add(pmd);
        var ppr = cmd.CreateParameter();
        ppr.ParameterName = "$pr";
        cmd.Parameters.Add(ppr);
        var pi = cmd.CreateParameter();
        pi.ParameterName = "$i";
        cmd.Parameters.Add(pi);
        var po = cmd.CreateParameter();
        po.ParameterName = "$o";
        cmd.Parameters.Add(po);
        var pc = cmd.CreateParameter();
        pc.ParameterName = "$c";
        cmd.Parameters.Add(pc);
        var ph = cmd.CreateParameter();
        ph.ParameterName = "$h";
        cmd.Parameters.Add(ph);
        var ps = cmd.CreateParameter();
        ps.ParameterName = "$s";
        cmd.Parameters.Add(ps);
        var psrc = cmd.CreateParameter();
        psrc.ParameterName = "$src";
        cmd.Parameters.Add(psrc);
        var pest = cmd.CreateParameter();
        pest.ParameterName = "$est";
        cmd.Parameters.Add(pest);
        var pr = cmd.CreateParameter();
        pr.ParameterName = "$r";
        cmd.Parameters.Add(pr);
        var pse = cmd.CreateParameter();
        pse.ParameterName = "$sess";
        cmd.Parameters.Add(pse);
        var pws = cmd.CreateParameter();
        pws.ParameterName = "$ws";
        cmd.Parameters.Add(pws);
        var pd = cmd.CreateParameter();
        pd.ParameterName = "$d";
        cmd.Parameters.Add(pd);
        var pt = cmd.CreateParameter();
        pt.ParameterName = "$t";
        cmd.Parameters.Add(pt);
        var pdt = cmd.CreateParameter();
        pdt.ParameterName = "$dt";
        cmd.Parameters.Add(pdt);
        var pu = cmd.CreateParameter();
        pu.ParameterName = "$u";
        cmd.Parameters.Add(pu);
        foreach (var r in batch)
        {
            double hit = r.Input > 0 ? Math.Clamp((double)r.Cached / r.Input, 0, 1) : 0;
            pp.Value = r.ProviderId;
            ppn.Value = r.ProviderName;
            pm.Value = r.ModelId;
            pmd.Value = r.ModelDisplay;
            ppr.Value = r.Protocol;
            pi.Value = r.Input;
            po.Value = r.Output;
            pc.Value = r.Cached;
            ph.Value = hit;
            ps.Value = r.Status;
            psrc.Value = UsageRecorder.NormalizeSource(r.Source, UsageRecorder.SourceAgent);
            pest.Value = r.Estimated != 0 ? 1 : 0;
            pr.Value = r.RequestId;
            pse.Value = r.SessionId;
            pws.Value = r.WorkspaceId;
            pd.Value = d;
            pt.Value = t;
            pdt.Value = dt;
            pu.Value = utc;
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
            case "today":
                return (fmt(now), fmt(now));
            case "yesterday":
                return (fmt(now.AddDays(-1)), fmt(now.AddDays(-1)));
            case "last3":
                return (fmt(now.AddDays(-2)), fmt(now));
            case "last7":
                return (fmt(now.AddDays(-6)), fmt(now));
            case "last30":
                return (fmt(now.AddDays(-29)), fmt(now));
            case "thisWeek":
            {
                // 周一起算：周日(0)→-6，其余 -(dow-1)
                int dow = (int)now.DayOfWeek;
                int back = dow == 0 ? 6 : dow - 1;
                return (fmt(now.AddDays(-back)), fmt(now));
            }
            case "thisMonth":
                return (fmt(new DateTime(now.Year, now.Month, 1)), fmt(now));
            case "all":
                return ("0000-00-00", "9999-99-99");
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
            default:
                throw new ArgumentException($"未知 preset：'{preset}'。");
        }
    }

    private static void ApplyScope(SqliteCommand cmd, UsageFilter f, List<string> conds)
    {
        var pids = (f?.ProviderIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        if (pids.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < pids.Count; i++)
            {
                string n = "$pid" + i;
                names.Add(n);
                var p = cmd.CreateParameter();
                p.ParameterName = n;
                p.Value = pids[i];
                cmd.Parameters.Add(p);
            }
            conds.Add($"provider_id IN ({string.Join(",", names)})");
        }
        var mks = (f?.ModelKeys ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        if (mks.Count > 0)
        {
            var orParts = new List<string>();
            for (int i = 0; i < mks.Count; i++)
            {
                string[] sp = mks[i].Split('\x1f');
                if (sp.Length != 2)
                    continue;
                string a = "$mkp" + i,
                    b = "$mkm" + i;
                var pa = cmd.CreateParameter();
                pa.ParameterName = a;
                pa.Value = sp[0];
                cmd.Parameters.Add(pa);
                var pb = cmd.CreateParameter();
                pb.ParameterName = b;
                pb.Value = sp[1];
                cmd.Parameters.Add(pb);
                orParts.Add($"(provider_id={a} AND model_id={b})");
            }
            if (orParts.Count > 0)
                conds.Add("(" + string.Join(" OR ", orParts) + ")");
        }
        // need12-P2：来源筛选（非法值忽略，空=全部）；估算筛选（null=全部）。
        var srcs = (f?.Sources ?? new List<string>())
            .Where(x => UsageRecorder.IsValidSource(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (srcs.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < srcs.Count; i++)
            {
                string n = "$src" + i;
                names.Add(n);
                var p = cmd.CreateParameter();
                p.ParameterName = n;
                p.Value = srcs[i];
                cmd.Parameters.Add(p);
            }
            conds.Add($"source IN ({string.Join(",", names)})");
        }
        if (f?.EstimatedOnly == true)
            conds.Add("estimated = 1");
        else if (f?.EstimatedOnly == false)
            conds.Add("estimated = 0");
    }
}
