using System.Text;
using Microsoft.Data.Sqlite;

namespace HaloAgent.Engine.Storage.Usage;

/// <summary>
/// P16 用量持久化：SQLite WAL + 短连接 + 进程内 lock(_gate)。
/// 跨进程（CLI+Server 双开）靠 busy_timeout + 唯一索引兜底。
/// 铁律：只依赖 Engine.Abstractions + Microsoft.Data.Sqlite，禁止引用 Engine.Core。
/// </summary>
public sealed class UsageStore
{
    private readonly string _dbFile;
    private readonly object _gate = new();
    private volatile bool _created;
    private volatile bool _degraded;
    private long _cachedSizeBytes;
    private long _cachedRowCount;
    private long _cachedStatsMs;

    public const int ExportLimit = 50000;

    public bool Degraded => _degraded;

    public UsageStore(string dbFile)
    {
        _dbFile = dbFile;
    }

    /// <summary>建库+DDL+PRAGMA，幂等。库损坏则改名重建并置 degraded。</summary>
    public void EnsureCreated()
    {
        if (_created && !_degraded) return;
        lock (_gate)
        {
            if (_created && !_degraded) return;
            try
            {
                var dir = Path.GetDirectoryName(_dbFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var conn = Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS usage_events(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_ms INTEGER NOT NULL,
  session_id TEXT NOT NULL,
  turn_id TEXT NOT NULL,
  provider_id TEXT NOT NULL,
  provider_display TEXT NOT NULL DEFAULT '',
  model_id TEXT NOT NULL,
  model_display TEXT NOT NULL DEFAULT '',
  protocol TEXT NOT NULL DEFAULT 'chat',
  prompt_tokens INTEGER NOT NULL DEFAULT 0,
  cached_tokens INTEGER NOT NULL DEFAULT 0,
  completion_tokens INTEGER NOT NULL DEFAULT 0,
  total_tokens INTEGER NOT NULL DEFAULT 0,
  truncated INTEGER NOT NULL DEFAULT 0,
  recorded_ms INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_turn ON usage_events(session_id, turn_id);
CREATE INDEX IF NOT EXISTS idx_usage_ts ON usage_events(ts_ms);
CREATE INDEX IF NOT EXISTS idx_usage_pm ON usage_events(provider_id, model_id, ts_ms);
CREATE INDEX IF NOT EXISTS idx_usage_session ON usage_events(session_id, ts_ms);";
                    cmd.ExecuteNonQuery();
                }
                _created = true;
                _degraded = false;
            }
            catch (SqliteException)
            {
                // 库损坏：改名后建新库
                try
                {
                    if (File.Exists(_dbFile))
                    {
                        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
                        var corrupt = _dbFile + ".corrupt." + stamp + ".db";
                        File.Move(_dbFile, corrupt, overwrite: false);
                    }
                }
                catch { }
                try
                {
                    using var conn2 = Open();
                    using var cmd2 = conn2.CreateCommand();
                    cmd2.CommandText = @"
CREATE TABLE IF NOT EXISTS usage_events(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_ms INTEGER NOT NULL,
  session_id TEXT NOT NULL,
  turn_id TEXT NOT NULL,
  provider_id TEXT NOT NULL,
  provider_display TEXT NOT NULL DEFAULT '',
  model_id TEXT NOT NULL,
  model_display TEXT NOT NULL DEFAULT '',
  protocol TEXT NOT NULL DEFAULT 'chat',
  prompt_tokens INTEGER NOT NULL DEFAULT 0,
  cached_tokens INTEGER NOT NULL DEFAULT 0,
  completion_tokens INTEGER NOT NULL DEFAULT 0,
  total_tokens INTEGER NOT NULL DEFAULT 0,
  truncated INTEGER NOT NULL DEFAULT 0,
  recorded_ms INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_turn ON usage_events(session_id, turn_id);
CREATE INDEX IF NOT EXISTS idx_usage_ts ON usage_events(ts_ms);
CREATE INDEX IF NOT EXISTS idx_usage_pm ON usage_events(provider_id, model_id, ts_ms);
CREATE INDEX IF NOT EXISTS idx_usage_session ON usage_events(session_id, ts_ms);";
                    cmd2.ExecuteNonQuery();
                }
                catch { }
                _created = true;
                _degraded = true;
            }
            catch
            {
                _created = true;
                _degraded = true;
            }
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection("Data Source=" + _dbFile);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private static void AddParam(SqliteCommand cmd, string name, object value)
    {
        cmd.Parameters.AddWithValue(name, value);
    }

    /// <summary>INSERT OR IGNORE（session_id, turn_id 幂等）。</summary>
    public void Record(UsageEvent e)
    {
        EnsureCreated();
        lock (_gate)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO usage_events
(ts_ms, session_id, turn_id, provider_id, provider_display, model_id, model_display, protocol,
 prompt_tokens, cached_tokens, completion_tokens, total_tokens, truncated, recorded_ms)
VALUES (@ts,@sid,@tid,@pid,@pdisp,@mid,@mdisp,@proto,@p,@c,@o,@t,@tr,@rec);";
                AddParam(cmd, "@ts", e.TsMs);
                AddParam(cmd, "@sid", e.SessionId ?? "");
                AddParam(cmd, "@tid", e.TurnId ?? "");
                AddParam(cmd, "@pid", e.ProviderId ?? "");
                AddParam(cmd, "@pdisp", e.ProviderDisplay ?? "");
                AddParam(cmd, "@mid", e.ModelId ?? "");
                AddParam(cmd, "@mdisp", e.ModelDisplay ?? "");
                AddParam(cmd, "@proto", string.IsNullOrEmpty(e.Protocol) ? "chat" : e.Protocol);
                AddParam(cmd, "@p", Math.Max(0, e.PromptTokens));
                AddParam(cmd, "@c", Math.Max(0, e.CachedTokens));
                AddParam(cmd, "@o", Math.Max(0, e.CompletionTokens));
                AddParam(cmd, "@t", e.PromptTokens + e.CompletionTokens);
                AddParam(cmd, "@tr", e.Truncated);
                AddParam(cmd, "@rec", e.RecordedMs);
                cmd.ExecuteNonQuery();
                _cachedStatsMs = 0; // 写后 stats 缓存失效
            }
            catch
            {
                // 落盘失败不抛，由调用方记日志
            }
        }
    }

    private static string BuildWhere(UsageQuery q, SqliteCommand cmd)
    {
        var sb = new StringBuilder("1=1");
        if (q.FromMs.HasValue) { sb.Append(" AND ts_ms >= @from"); AddParam(cmd, "@from", q.FromMs.Value); }
        if (q.ToMs.HasValue) { sb.Append(" AND ts_ms <= @to"); AddParam(cmd, "@to", q.ToMs.Value); }
        if (!string.IsNullOrEmpty(q.ProviderId)) { sb.Append(" AND provider_id = @pid"); AddParam(cmd, "@pid", q.ProviderId); }
        if (!string.IsNullOrEmpty(q.ModelId)) { sb.Append(" AND model_id = @mid"); AddParam(cmd, "@mid", q.ModelId); }
        if (!string.IsNullOrEmpty(q.SessionId)) { sb.Append(" AND session_id = @sid"); AddParam(cmd, "@sid", q.SessionId); }
        return sb.ToString();
    }

    private static UsageEvent ReadEvent(SqliteDataReader r)
    {
        return new UsageEvent
        {
            TsMs = r.GetInt64(0),
            SessionId = r.GetString(1),
            TurnId = r.GetString(2),
            ProviderId = r.GetString(3),
            ProviderDisplay = r.IsDBNull(4) ? "" : r.GetString(4),
            ModelId = r.GetString(5),
            ModelDisplay = r.IsDBNull(6) ? "" : r.GetString(6),
            Protocol = r.IsDBNull(7) ? "chat" : r.GetString(7),
            PromptTokens = r.GetInt64(8),
            CachedTokens = r.GetInt64(9),
            CompletionTokens = r.GetInt64(10),
            TotalTokens = r.GetInt64(11),
            Truncated = r.GetInt32(12),
            RecordedMs = r.GetInt64(13),
        };
    }

    /// <summary>分页查询，恒 desc。</summary>
    public (IReadOnlyList<UsageEvent> Items, long Total) Query(UsageQuery q)
    {
        EnsureCreated();
        int page = Math.Max(1, q.Page);
        int pageSize = Math.Clamp(q.PageSize <= 0 ? 50 : q.PageSize, 1, 200);
        lock (_gate)
        {
            try
            {
                using var conn = Open();
                long total;
                using (var cnt = conn.CreateCommand())
                {
                    cnt.CommandText = "SELECT COUNT(*) FROM usage_events WHERE " + BuildWhere(q, cnt);
                    total = (long)(cnt.ExecuteScalar() ?? 0L);
                }
                var items = new List<UsageEvent>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT ts_ms, session_id, turn_id, provider_id, provider_display,"
                        + " model_id, model_display, protocol, prompt_tokens, cached_tokens,"
                        + " completion_tokens, total_tokens, truncated, recorded_ms"
                        + " FROM usage_events WHERE " + BuildWhere(q, cmd)
                        + " ORDER BY ts_ms DESC, id DESC LIMIT @lim OFFSET @off;";
                    AddParam(cmd, "@lim", pageSize);
                    AddParam(cmd, "@off", (long)(page - 1) * pageSize);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) items.Add(ReadEvent(r));
                }
                return (items, total);
            }
            catch
            {
                return (Array.Empty<UsageEvent>(), 0);
            }
        }
    }

    /// <summary>汇总：totals + byDay（按前端时区切天，空桶补0）+ byModel（双键）。</summary>
    public UsageSummary Summary(UsageQuery q)
    {
        EnsureCreated();
        var s = new UsageSummary { Degraded = _degraded };
        lock (_gate)
        {
            try
            {
                using var conn = Open();
                var rows = new List<UsageEvent>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT ts_ms, session_id, turn_id, provider_id, provider_display,"
                        + " model_id, model_display, protocol, prompt_tokens, cached_tokens,"
                        + " completion_tokens, total_tokens, truncated, recorded_ms"
                        + " FROM usage_events WHERE " + BuildWhere(q, cmd) + " ORDER BY ts_ms ASC;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) rows.Add(ReadEvent(r));
                }
                long input = 0, cached = 0, output = 0;
                foreach (var e in rows)
                {
                    input += e.PromptTokens;
                    cached += e.CachedTokens;
                    output += e.CompletionTokens;
                }
                s.Input = input;
                s.Cached = cached;
                s.Output = output;
                s.Total = input + output;
                s.Requests = rows.Count;
                s.HitRate = input > 0 ? (double)cached / input : 0;
                s.ByDay = BuildByDay(rows, q);
                s.ByModel = BuildByModel(rows);
                return s;
            }
            catch
            {
                return s;
            }
        }
    }

    private static List<UsageByDay> BuildByDay(List<UsageEvent> rows, UsageQuery q)
    {
        var map = new Dictionary<string, UsageByDay>(StringComparer.Ordinal);
        foreach (var e in rows)
        {
            // 后端按 ts_ms + tzOffsetMin 分桶：本地毫秒 = ts + (-tzOffset)*60000
            long localMs = e.TsMs - (long)q.TzOffsetMin * 60000L;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(localMs).UtcDateTime;
            string day = $"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}";
            if (!map.TryGetValue(day, out var d)) { d = new UsageByDay { Day = day }; map[day] = d; }
            d.Input += e.PromptTokens;
            d.Cached += e.CachedTokens;
            d.Output += e.CompletionTokens;
        }
        // 空桶补0：from→to 连续日期（仅当双传时）
        if (q.FromMs.HasValue && q.ToMs.HasValue && q.FromMs.Value <= q.ToMs.Value)
        {
            long fromLocal = q.FromMs.Value - (long)q.TzOffsetMin * 60000L;
            long toLocal = q.ToMs.Value - (long)q.TzOffsetMin * 60000L;
            var fromDay = DateTimeOffset.FromUnixTimeMilliseconds(fromLocal).UtcDateTime.Date;
            var toDay = DateTimeOffset.FromUnixTimeMilliseconds(toLocal).UtcDateTime.Date;
            for (var d = fromDay; d <= toDay; d = d.AddDays(1))
            {
                string key = $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
                if (!map.ContainsKey(key)) map[key] = new UsageByDay { Day = key };
            }
        }
        return map.Values.OrderBy(x => x.Day, StringComparer.Ordinal).ToList();
    }

    private static List<UsageByModel> BuildByModel(List<UsageEvent> rows)
    {
        var map = new Dictionary<string, UsageByModel>(StringComparer.Ordinal);
        foreach (var e in rows)
        {
            string key = e.ProviderId + "\u0001" + e.ModelId;
            if (!map.TryGetValue(key, out var m))
            {
                m = new UsageByModel
                {
                    ProviderId = e.ProviderId,
                    ProviderDisplay = e.ProviderDisplay,
                    ModelId = e.ModelId,
                    ModelDisplay = e.ModelDisplay,
                };
                map[key] = m;
            }
            m.Input += e.PromptTokens;
            m.Cached += e.CachedTokens;
            m.Output += e.CompletionTokens;
            m.Requests++;
        }
        foreach (var m in map.Values)
            m.HitRate = m.Input > 0 ? (double)m.Cached / m.Input : 0;
        return map.Values.OrderByDescending(x => x.Input).ToList();
    }

    /// <summary>导出 CSV（RFC4180，全字段包引号，\r\n，调用方加 BOM）。上限 50000。</summary>
    public (string Csv, bool Truncated) ExportCsv(UsageQuery q)
    {
        EnsureCreated();
        lock (_gate)
        {
            try
            {
                using var conn = Open();
                var sb = new StringBuilder();
                sb.Append("\"时间\",\"session_id\",\"turn_id\",\"provider\",\"model\",\"protocol\","
                    + "\"输入\",\"缓存\",\"输出\",\"合计\",\"命中率\",\"截断\"\r\n");
                int count = 0;
                bool truncated = false;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT ts_ms, session_id, turn_id, provider_id, provider_display,"
                        + " model_id, model_display, protocol, prompt_tokens, cached_tokens,"
                        + " completion_tokens, total_tokens, truncated, recorded_ms"
                        + " FROM usage_events WHERE " + BuildWhere(q, cmd)
                        + " ORDER BY ts_ms DESC, id DESC LIMIT @lim;";
                    AddParam(cmd, "@lim", ExportLimit + 1);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        count++;
                        if (count > ExportLimit) { truncated = true; break; }
                        var e = ReadEvent(r);
                        string time = DateTimeOffset.FromUnixTimeMilliseconds(e.TsMs).ToLocalTime()
                            .ToString("yyyy-MM-dd HH:mm:ss");
                        double hit = e.PromptTokens > 0 ? (double)e.CachedTokens / e.PromptTokens : 0;
                        string[] fields = new[]
                        {
                            time, e.SessionId, e.TurnId, e.ProviderId, e.ModelId, e.Protocol,
                            e.PromptTokens.ToString(), e.CachedTokens.ToString(),
                            e.CompletionTokens.ToString(), e.TotalTokens.ToString(),
                            (hit * 100).ToString("F1") + "%", e.Truncated == 1 ? "1" : "0",
                        };
                        for (int i = 0; i < fields.Length; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append('"');
                            sb.Append(fields[i].Replace("\"", "\"\""));
                            sb.Append('"');
                        }
                        sb.Append("\r\n");
                    }
                }
                return (sb.ToString(), truncated);
            }
            catch
            {
                return ("", false);
            }
        }
    }

    public long ClearBefore(long beforeMs)
    {
        EnsureCreated();
        lock (_gate)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM usage_events WHERE ts_ms < @before;";
                AddParam(cmd, "@before", beforeMs);
                long removed = cmd.ExecuteNonQuery();
                _cachedStatsMs = 0;
                return removed;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>库大小 + 行数（PRAGMA page_count/page_size，结果缓存 5s）。</summary>
    public (long SizeBytes, long RowCount) Stats()
    {
        EnsureCreated();
        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _cachedStatsMs < 5000 && _cachedStatsMs > 0)
                return (_cachedSizeBytes, _cachedRowCount);
            try
            {
                using var conn = Open();
                long pages = 0, size = 0, rows = 0;
                using (var c1 = conn.CreateCommand()) { c1.CommandText = "PRAGMA page_count;"; pages = Convert.ToInt64(c1.ExecuteScalar()); }
                using (var c2 = conn.CreateCommand()) { c2.CommandText = "PRAGMA page_size;"; size = Convert.ToInt64(c2.ExecuteScalar()); }
                using (var c3 = conn.CreateCommand()) { c3.CommandText = "SELECT COUNT(*) FROM usage_events;"; rows = Convert.ToInt64(c3.ExecuteScalar() ?? 0L); }
                _cachedSizeBytes = pages * size;
                _cachedRowCount = rows;
                _cachedStatsMs = now;
                return (_cachedSizeBytes, _cachedRowCount);
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
