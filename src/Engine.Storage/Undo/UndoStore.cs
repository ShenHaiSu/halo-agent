using System.Text.Json;
using System.Text.Json.Serialization;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;

namespace HaloAgent.Engine.Storage.Undo;

/// <summary>
/// undo 快照：写文件前自动存一份，事后可回滚。
///
/// ============ 为什么必须做 ============
/// 模型改文件是"不可撤销"的高风险操作。没有快照，用户只能在改错后
/// 从 git 恢复（可能没提交）或者干脆认了。
///
/// ============ 结构 ============
///   undo/manifest.jsonl            全局清单（一行一条）
///   undo/files/&lt;session&gt;/&lt;ts&gt;-&lt;seq&gt;.bak   快照本体
///
/// ============ 保留策略 ============
///   最多 500 条，超出时删最旧的（含物理文件）。
/// </summary>
public sealed class UndoStore
{
    public const int MaxSnapshots = 500;

    public sealed record Snapshot
    {
        [JsonPropertyName("session")] public string Session { get; init; } = "";
        [JsonPropertyName("ts_ms")] public long TsMs { get; init; }
        [JsonPropertyName("orig")] public string Orig { get; init; } = "";
        [JsonPropertyName("snap")] public string Snap { get; init; } = "";
        [JsonPropertyName("action")] public string Action { get; init; } = "";
        [JsonPropertyName("gen")] public string? Gen { get; init; }

        [JsonIgnore]
        public bool IsExternal => Gen is not null && Gen != CurrentGen;
    }

    private const string CurrentGen = "h4l0u1n2d3o4";

    /// <summary>为指定会话生成快照。文件不存在（新建）返回 null。</summary>
    public static Snapshot? Backup(string sessionId, string file, string action)
    {
        try
        {
            if (!File.Exists(file)) return null;

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sid = SanitizeSession(sessionId);
            var sessionRoot = Path.Combine(Paths.UndoFilesDir, sid);
            Directory.CreateDirectory(sessionRoot);

            var all = ReadManifest();
            var seq = all.Count(s => s.Session == sid);
            var snapRel = $"{ts}-{seq:D4}.bak";
            var snapAbs = Path.Combine(sessionRoot, snapRel);

            File.Copy(file, snapAbs, overwrite: true);

            var record = new Snapshot
            {
                Session = sid,
                TsMs = ts,
                Orig = Path.GetFullPath(file),
                Snap = Path.Combine(sid, snapRel).Replace('\\', '/'),
                Action = action,
                Gen = CurrentGen,
            };

            all.Add(record);

            // 保留最近 N 条
            while (all.Count > MaxSnapshots)
            {
                var oldest = all[0];
                all.RemoveAt(0);
                try
                {
                    var p = Path.Combine(Paths.UndoFilesDir, oldest.Snap.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }

            WriteManifest(all);
            return record;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列出全部快照（新 → 旧）。</summary>
    public static List<Snapshot> List()
    {
        var all = ReadManifest();
        all.Reverse();
        return all;
    }

    /// <summary>回滚指定快照。回滚前先给当前状态再打一个快照（防误操作）。</summary>
    public static Snapshot Rollback(Snapshot snapshot)
    {
        var snapAbs = Path.Combine(Paths.UndoFilesDir,
            snapshot.Snap.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(snapAbs))
            throw new FileNotFoundException($"快照文件不存在: {snapAbs}");

        var orig = snapshot.Orig;
        var dir = Path.GetDirectoryName(orig);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 回滚前备份当前状态
        Backup(snapshot.Session, orig, $"rollback→{snapshot.TsMs}");

        File.Copy(snapAbs, orig, overwrite: true);
        return snapshot;
    }

    /// <summary>按序号回滚（1 = 最新）。</summary>
    public static Snapshot RollbackByIndex(int index)
    {
        var all = List();
        if (index < 1 || index > all.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"序号无效（共 {all.Count} 条）");

        var snap = all[index - 1];
        return Rollback(snap);
    }

    // ---------------- 内部 ----------------

    private static List<Snapshot> ReadManifest()
    {
        if (!File.Exists(Paths.UndoManifest)) return new List<Snapshot>();

        var result = new List<Snapshot>();
        foreach (var line in File.ReadLines(Paths.UndoManifest))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(line, StorageJson.Compact);
                if (s is not null) result.Add(s);
            }
            catch { }
        }
        return result;
    }

    private static void WriteManifest(List<Snapshot> snapshots)
    {
        var dir = Path.GetDirectoryName(Paths.UndoManifest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new System.Text.StringBuilder();
        foreach (var s in snapshots)
        {
            // 序列化失败不能静默吞：那会让刚落盘的快照"不在册"
            sb.AppendLine(JsonSerializer.Serialize(s, StorageJson.Compact));
        }
        File.WriteAllText(Paths.UndoManifest, sb.ToString());
    }

    private static string SanitizeSession(string id)
    {
        var cleaned = new string(id.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "default" : cleaned;
    }
}
