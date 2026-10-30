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

        foreach (var rawLine in File.ReadLines(path))
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
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;

                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(line); }
                    catch { continue; }

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

                        var body = root.TryGetProperty("msg", out var inner) && inner.ValueKind == JsonValueKind.Object
                            ? inner : root;
                        if (!body.TryGetProperty("role", out var roleEl) ||
                            roleEl.ValueKind != JsonValueKind.String) continue;

                        var role = roleEl.GetString();
                        if (role == "user")
                        {
                            userMsgs++;
                            if (title.Length == 0 &&
                                body.TryGetProperty("content", out var c) &&
                                c.ValueKind == JsonValueKind.String)
                            {
                                title = (c.GetString() ?? "").Trim().Replace('\n', ' ');
                                if (title.Length > 60) title = title[..60] + "…";
                            }
                        }
                        else if (role == "assistant")
                        {
                            assistantMsgs++;
                        }
                    }
                }

                var notePath = Path.ChangeExtension(file, ".meta.json");
                string? note = null;
                if (File.Exists(notePath))
                {
                    try
                    {
                        using var nd = JsonDocument.Parse(File.ReadAllText(notePath));
                        if (nd.RootElement.TryGetProperty("note", out var nv) &&
                            nv.ValueKind == JsonValueKind.String)
                            note = nv.GetString();
                    }
                    catch { }
                }

                entries.Add(new HistoryEntry
                {
                    Path = file,
                    Id = Path.GetFileNameWithoutExtension(file),
                    Title = string.IsNullOrEmpty(title) ? "(空会话)" : title,
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
