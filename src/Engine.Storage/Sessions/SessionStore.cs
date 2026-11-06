using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;

namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 会话历史存储（JSONL）。
///
/// ============ 文件格式 ============
/// 首行 meta（懒写）：
///   {"meta":{"headless":false,"schema":"...","cwd":"C:\\work"}}
/// 之后每行一条消息。
///
/// ============ 三条不变式 ============
/// 1. **懒创建**：首次 record 时才建文件（避免"开了就退"留下 0 字节空壳）
/// 2. **已有内容不写 meta**：resume 后不能重复写 meta（否则每次恢复多一行）
/// 3. **追加写 + 立即 flush**：进程崩溃时最多丢最后一条，不丢全部
/// </summary>
public sealed class SessionStore : IDisposable
{
    private const string HistorySchema = "h4l0a1g2e3n4";

    private readonly object _lock = new();
    private string? _path;
    private StreamWriter? _writer;

    public string SessionId { get; private set; } = NewSessionId();
    public string? HistoryPath => _path;

    private static string NewSessionId()
    {
        // s-yyyyMMdd-HHmmss-xxxx
        return $"s-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    /// <summary>切换到某个历史文件（resume 用）。</summary>
    public void AttachTo(string path)
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _path = path;
            SessionId = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>新建会话（换身份、换文件）。</summary>
    public void StartNew()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            try { if (_path is not null) { var old = ResponseIdPath(_path); if (File.Exists(old)) File.Delete(old); } } catch { }
            SessionId = NewSessionId();
            Directory.CreateDirectory(Paths.SessionsDir);
            _path = Path.Combine(Paths.SessionsDir, SessionId + ".jsonl");
        }
    }
    private static string ResponseIdPath(string historyPath) => Path.Combine(Path.GetDirectoryName(historyPath) ?? ".", Path.GetFileNameWithoutExtension(historyPath) + ".response_id");
    public void SaveResponseId(string? id)
    {
        string? p;
        lock (_lock) p = _path;
        if (p is null) return;
        var sp = ResponseIdPath(p);
        try { if (id is null) { if (File.Exists(sp)) File.Delete(sp); } else File.WriteAllText(sp, id); } catch { }
    }
    public string? LoadResponseId()
    {
        string? p;
        lock (_lock) p = _path;
        if (p is null) return null;
        var sp = ResponseIdPath(p);
        try { if (!File.Exists(sp)) return null; var t = File.ReadAllText(sp).Trim(); return string.IsNullOrEmpty(t) ? null : t; } catch { return null; }
    }

    /// <summary>清空（截断文件，保留路径）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            if (_path is null) return;
            var sp = ResponseIdPath(_path);
            try
            {
                if (File.Exists(_path))
                    using (new FileStream(_path, FileMode.Truncate)) { }
                if (File.Exists(sp)) File.Delete(sp);
            }
            catch { }
        }
    }

    /// <summary>记录一条消息（内存由调用方维护，这里只落盘）。</summary>
    public void Record(StoredMessage msg, bool headless, string cwd)
    {
        lock (_lock)
        {
            EnsureWriter(headless, cwd);
            if (_writer is null) return;

            try
            {
                var json = JsonSerializer.Serialize(msg, StorageJson.Compact);
                _writer.WriteLine(json);
                _writer.Flush();
            }
            catch { /* 落盘失败不中断对话 */ }
        }
    }

     private void EnsureWriter(bool headless, string cwd)
     {
         if (_writer is not null) return;
         if (_path is null)
         {
             Directory.CreateDirectory(Paths.SessionsDir);
             _path = Path.Combine(Paths.SessionsDir, SessionId + ".jsonl");
         }

         var fresh = !File.Exists(_path) || new FileInfo(_path).Length == 0;
         Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
         var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
         _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

         if (fresh)
         {
             var meta = JsonSerializer.Serialize(new
             {
                 meta = new { headless, schema = HistorySchema, cwd },
             }, StorageJson.Compact);
             _writer.WriteLine(meta);
             _writer.Flush();
         }
     }

     /// <summary>向历史文件末尾单向追加一条上下文压缩标记记录。</summary>
     public void RecordCompactMarker(StoredCompactMarker marker, bool headless = false, string cwd = "")
     {
         lock (_lock)
         {
             EnsureWriter(headless, cwd);
             var line = JsonSerializer.Serialize(marker, StorageJson.Compact);
             _writer!.WriteLine(line);
             _writer!.Flush();
         }
     }

     /// <summary>只换首行 meta.cwd（原子替换）。无文件/0字节则跳过，下次 Record 自然带新 cwd。</summary>
     public void UpdateCwdAtomic(string newCwd)
     {
         string? path;
         lock (_lock)
         {
             path = _path;
             try { _writer?.Flush(); } catch { }
             try { _writer?.Dispose(); } catch { }
             _writer = null;
         }
         if (string.IsNullOrEmpty(path)) return;
         try
         {
             if (!File.Exists(path)) return;
             if (new FileInfo(path).Length == 0) return;
             var lines = File.ReadAllLines(path, new UTF8Encoding(false));
             if (lines.Length == 0) return;
             bool hasMeta = false;
             try
             {
                 using var doc = JsonDocument.Parse(lines[0].Trim());
                 hasMeta = doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("meta", out _);
             }
             catch { }
             if (!hasMeta) return;
             HistoryMeta? parsed = null;
             try { parsed = JsonSerializer.Deserialize<HistoryMeta>(lines[0].Trim(), StorageJson.Compact); } catch { }
             var headless = parsed?.Meta?.Headless ?? false;
             var schema = parsed?.Meta?.Schema ?? HistorySchema;
             var newMeta = JsonSerializer.Serialize(new
             {
                 meta = new { headless, schema, cwd = newCwd },
             }, StorageJson.Compact);
             lines[0] = newMeta;
             var dir = Path.GetDirectoryName(path) ?? ".";
             var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
             File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
             try
             {
                 using var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                 fs.Flush(true);
             }
             catch { }
             File.Replace(tmp, path, null);
         }
         catch { }
     }
 
     /// <summary>压缩后原子重写历史文件（tmp+rename，与 UpdateCwdAtomic 同口径）。
     /// 入参为压缩后应落盘的全部消息（不含 system，与 ReadHistory 对称：文件从不存 system）。
     /// 成功则文件变小，失败抛异常（调用方判为压缩失败，内存不换、不断链、不发 compacted）。</summary>
     public void RewriteAfterCompact(IReadOnlyList<StoredMessage> keepPlusSummary)
     {
         string? path;
         lock (_lock)
         {
             path = _path;
             try { _writer?.Flush(); } catch { }
             try { _writer?.Dispose(); } catch { }
             _writer = null;
         }
         if (string.IsNullOrEmpty(path))
             throw new InvalidOperationException("无历史文件路径，无法重写压缩结果");
         var dir = Path.GetDirectoryName(path) ?? ".";
         Directory.CreateDirectory(dir);
         // 保留首行 meta（headless/schema/cwd）
         bool headless = false;
         string schema = HistorySchema;
         string? cwd = null;
         bool hasFile = File.Exists(path) && new FileInfo(path).Length > 0;
         if (hasFile)
         {
             try
             {
                 string? first = null;
                 using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                 using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
                     first = sr.ReadLine();
                 if (!string.IsNullOrWhiteSpace(first))
                 {
                     using var doc = JsonDocument.Parse(first.Trim());
                     if (doc.RootElement.ValueKind == JsonValueKind.Object
                         && doc.RootElement.TryGetProperty("meta", out _))
                     {
                         try
                         {
                             var parsed = JsonSerializer.Deserialize<HistoryMeta>(first.Trim(), StorageJson.Compact);
                             headless = parsed?.Meta?.Headless ?? false;
                             schema = parsed?.Meta?.Schema ?? HistorySchema;
                             cwd = parsed?.Meta?.Cwd;
                         }
                         catch { }
                     }
                 }
             }
             catch (Exception ex) { throw new InvalidOperationException("读取历史 meta 失败: " + ex.Message, ex); }
         }
         var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
         try
         {
             using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
             using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
             {
                 var meta = JsonSerializer.Serialize(new
                 {
                     meta = new { headless, schema, cwd },
                 }, StorageJson.Compact);
                 sw.WriteLine(meta);
                 foreach (var m in keepPlusSummary)
                 {
                     if (m.Role == StoredRole.System) continue;
                     sw.WriteLine(JsonSerializer.Serialize(m, StorageJson.Compact));
                 }
                 sw.Flush();
                 fs.Flush(true);
             }
             File.Replace(tmp, path, null);
         }
         catch
         {
             try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
             throw;
         }
     }
 

    /// <summary>
    /// 读取历史文件。返回 (meta, messages)。
    /// **自动修复孤儿 tool_calls**（见 HistoryRepair）。
    /// </summary>
    public static (HistoryMeta? meta, List<StoredMessage> messages, int repaired)
        ReadHistory(string path)
    {
        HistoryMeta? meta = null;
        var messages = new List<StoredMessage>();

         if (!File.Exists(path)) return (null, messages, 0);

         // 允许与追加写句柄并发读（writer 以 FileShare.Read 打开，读端需 ReadWrite 共享）。
         IEnumerable<string> ReadAllLinesShared(string p)
         {
             using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
             using var sr = new StreamReader(fs, new UTF8Encoding(false));
             string? line;
             while ((line = sr.ReadLine()) is not null) yield return line;
         }
         foreach (var rawLine in ReadAllLinesShared(path))
         {
             var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }   // 半截行（崩溃时没写完）直接跳过

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("meta", out _))
                {
                    try { meta = JsonSerializer.Deserialize<HistoryMeta>(line, StorageJson.Compact); }
                    catch { }
                    continue;
                }

                if (root.TryGetProperty("marker", out var markerProp) &&
                    string.Equals(markerProp.GetString(), "compact", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var marker = JsonSerializer.Deserialize<StoredCompactMarker>(line, StorageJson.Compact);
                        if (marker != null)
                        {
                            messages.Add(new StoredMessage
                            {
                                Marker = "compact",
                                Seq = marker.Seq,
                                Summary = marker.Summary,
                                RemovedTurns = marker.RemovedTurns,
                                KeptTurns = marker.KeptTurns,
                                TsMs = marker.TsMs,
                            });
                        }
                    }
                    catch { }
                    continue;
                }

                try
                {
                    StoredMessage? m = null;
                    // 兼容 {"msg":{...}} 包装与裸消息
                    if (root.TryGetProperty("msg", out var inner) && inner.ValueKind == JsonValueKind.Object)
                    {
                        m = JsonSerializer.Deserialize<StoredMessage>(inner.GetRawText(), StorageJson.Compact);
                    }
                    else
                    {
                        m = JsonSerializer.Deserialize<StoredMessage>(line, StorageJson.Compact);
                    }

                    if (m is null) continue;
                    if (m.Role == StoredRole.System) continue;   // 系统提示由当前状态重建
                    messages.Add(m);
                }
                catch { /* 坏行跳过 */ }
            }
        }

        var repaired = HistoryRepair.Repair(messages);
        return (meta, messages, repaired);
    }

    /// <summary>列出历史会话（新 → 旧）。</summary>
    public static List<HistoryEntry> ListHistory()
    {
        var dir = Paths.SessionsDir;
        if (!Directory.Exists(dir)) return new List<HistoryEntry>();

        var entries = new List<HistoryEntry>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                HistoryMeta? meta = null;
                string title = "";
                int userMsgs = 0, assistantMsgs = 0;

                foreach (var rawLine in File.ReadLines(file))
                {
                    ScanSummaryLine(rawLine, ref meta, ref title, ref userMsgs, ref assistantMsgs);
                }

                var note = ReadNote(file);

                entries.Add(new HistoryEntry
                {
                    Path = file,
                    Id = Path.GetFileNameWithoutExtension(file),
                    Title = string.IsNullOrEmpty(title) ? SessionTitle.EmptyTitle : title,
                    Note = note,
                    Headless = meta?.Meta?.Headless ?? false,
                    Cwd = meta?.Meta?.Cwd,
                    MtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds(),
                    UserMsgs = userMsgs,
                    AssistantMsgs = assistantMsgs,
                });
            }
            catch { }
        }

        return entries.OrderByDescending(e => e.MtimeMs).ToList();
    }

    public static void DeleteHistory(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var notePath = Path.ChangeExtension(path, ".meta.json");
        try { if (File.Exists(notePath)) File.Delete(notePath); } catch { }
    }

    private static string? ReadNote(string file)
    {
        var notePath = Path.ChangeExtension(file, ".meta.json");
        if (!File.Exists(notePath)) return null;
        try
        {
            using var nd = JsonDocument.Parse(File.ReadAllText(notePath));
            if (nd.RootElement.TryGetProperty("note", out var nv) &&
                nv.ValueKind == JsonValueKind.String)
                return nv.GetString();
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, new UTF8Encoding(false));
        string? line;
        while ((line = sr.ReadLine()) is not null) yield return line;
    }

    private static void ScanSummaryLine(string rawLine, ref HistoryMeta? meta, ref string title, ref int userMsgs, ref int assistantMsgs)    {
        var line = rawLine.Trim();
        if (line.Length == 0) return;
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(line); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("meta", out _))
            {
                try { meta = JsonSerializer.Deserialize<HistoryMeta>(line, StorageJson.Compact); }
                catch { }
                return;
            }
            var body = root.TryGetProperty("msg", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner : root;
            if (!body.TryGetProperty("role", out var roleEl) ||
                roleEl.ValueKind != JsonValueKind.String) return;
            var role = roleEl.GetString();
            if (role == "user")
            {
                userMsgs++;
                if (title.Length == 0 &&
                    body.TryGetProperty("content", out var c) &&
                    c.ValueKind == JsonValueKind.String)
                {
                    var candidate = SessionTitle.Build(c.GetString());
                    if (!SessionTitle.IsEmpty(candidate)) title = candidate;
                }
            }
            else if (role == "assistant")
            {
                assistantMsgs++;
            }
        }
    }

    /// <summary>
    /// 单条会话摘要（供 turn.finished 捎带）。
    /// 只读目标文件：首条 role==user 的 content 算标题 + 计数 + mtime；note 侧读 .meta.json。
    /// 文件不存在返回 null（调用方不发 session 字段，降级由前端处理）。
    /// 任何 IO/解析异常返回 null，绝不抛（收尾路径禁止因标题中断 turn）。
    /// </summary>
    public HistoryEntry? GetSummary(string sessionId)
    {
        string path;
        lock (_lock)
        {
            if (_path is null) return null;
            // 仅支持当前会话（跨会话查询走 ListHistory；收尾帧只查自己）。
            if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal)) return null;
            path = _path;
        }
        try
        {
            if (!File.Exists(path)) return null;
            string title = "";
            int userMsgs = 0, assistantMsgs = 0;
            HistoryMeta? ignoredMeta = null;
            foreach (var rawLine in ReadAllLinesShared(path))
            {
                ScanSummaryLine(rawLine, ref ignoredMeta, ref title, ref userMsgs, ref assistantMsgs);
            }
            var note = ReadNote(path);
            return new HistoryEntry
            {
                Path = path,
                Id = sessionId,
                Title = string.IsNullOrEmpty(title) ? SessionTitle.EmptyTitle : title,
                Note = note,
                Headless = false,
                Cwd = null,
                MtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(),
                UserMsgs = userMsgs,
                AssistantMsgs = assistantMsgs,
            };
        }
        catch { return null; }
    }

    public static void WriteNote(string path, string? note)
    {
        var notePath = Path.ChangeExtension(path, ".meta.json");
        try
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                if (File.Exists(notePath)) File.Delete(notePath);
                return;
            }
            File.WriteAllText(notePath,
                JsonSerializer.Serialize(new { note }, StorageJson.Compact));
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public sealed class HistoryMeta
{
    [JsonPropertyName("meta")] public MetaBody? Meta { get; set; }
}

public sealed class MetaBody
{
    [JsonPropertyName("headless")] public bool Headless { get; set; }
    [JsonPropertyName("schema")] public string? Schema { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
}

public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public bool Headless { get; set; }
    public string? Cwd { get; set; }
    public long MtimeMs { get; set; }
    public int UserMsgs { get; set; }
    public int AssistantMsgs { get; set; }
}
