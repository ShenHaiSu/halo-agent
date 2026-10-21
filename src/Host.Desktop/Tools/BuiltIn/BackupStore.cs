using System.Text.Json;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need15 P2-2：备份存储（命名/prune/meta/恢复）。
/// 路径：&lt;DataRoot&gt;/backups/&lt;workspaceId&gt;/&lt;sessionId&gt;/yyyyMMdd-HHmmss-fff_&lt;hash16&gt;.bak + 同名 .meta.json + 会话 index.json
/// P0-2 先行复用：WriteBackup + Prune。
/// need22-P0 S4：tmp+Move+fsync 原子写、Prune 按 mtime、文件名 hash 定长 hex、
/// 全局配额 500MB（可配）、备份索引 index.json 精确回查（替代 GuessOriginal 文件名猜测）。
/// </summary>
internal static class BackupStore
{
    /// <summary>每会话最多保留备份数（单测可调，线上热调参考 AskUserGate 模式）。</summary>
    internal static int KeepPerSession = 50;

    /// <summary>need22-P0 S4：全局配额（默认 500MB，可配；超限 WriteBackup 抛 BACKUP_QUOTA_EXCEEDED）。</summary>
    internal static long GlobalQuotaBytes = 500L * 1024 * 1024;

    internal const string QuotaExceededCode = "BACKUP_QUOTA_EXCEEDED";

    private static readonly object _backupLock = new();

    internal sealed class BackupMeta
    {
        public string Src { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string? Tool { get; set; }
        public string At { get; set; } = string.Empty;
    }

    internal sealed class BackupIndexEntry
    {
        public string Bak { get; set; } = string.Empty;
        public string Src { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string? Tool { get; set; }
        public string At { get; set; } = string.Empty;
    }

    /// <summary>落盘前备份原文件。返回 (bak绝对路径, meta绝对路径, 原文件hash)。文件不存在则返回 (null,null,null)。</summary>
    internal static (string? BakPath, string? MetaPath, string? Hash) WriteBackup(
        string fullPath,
        string dataRoot,
        string workspaceId,
        string sessionId,
        string? tool = null
    )
    {
        if (!File.Exists(fullPath))
        {
            return (null, null, null);
        }

        byte[] bytes = File.ReadAllBytes(fullPath);
        string hash = FileToolShared.ComputeHash(bytes);
        string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
        string ws = string.IsNullOrWhiteSpace(workspaceId) ? "ws" : workspaceId;
        string sess = string.IsNullOrWhiteSpace(sessionId) ? "sess" : sessionId;
        string dir = Path.Combine(root, "backups", ws, sess);
        Directory.CreateDirectory(dir);

        lock (_backupLock)
        {
            // need22-P0 S4：全局配额（超限拒绝并告警 BACKUP_QUOTA_EXCEEDED；调用方阻塞写盘）
            long used = GetGlobalBackupBytes(root);
            if (used + bytes.Length > GlobalQuotaBytes)
            {
                try
                {
                    HaloLogger.App.Warn(
                        $"[Backup] {QuotaExceededCode}：全局备份已用 {used} 字节，本次 +{bytes.Length} 将超配额 {GlobalQuotaBytes}。写入已阻塞。"
                    );
                }
                catch
                {
                    // 日志失败不影响拒绝语义
                }
                throw new InvalidOperationException(
                    $"{QuotaExceededCode}：备份配额超限（已用 {used} 字节/配额 {GlobalQuotaBytes} 字节）。已拒绝本次备份，写盘已中止以防无退路覆盖；请清理 <DataRoot>/backups 后重试。"
                );
            }

            // need22-P0 S4：文件名定长 hex（stamp + 内容 hash16，不再嵌入 basename，消除 GuessOriginal 歧义）
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string shortHash = hash.Length >= 16 ? hash.Substring(0, 16) : hash.PadRight(16, '0');
            string name = $"{stamp}_{shortHash}.bak";
            string bakPath = Path.Combine(dir, name);
            int dup = 0;
            while (File.Exists(bakPath) && dup < 1000)
            {
                dup++;
                bakPath = Path.Combine(dir, $"{stamp}_{shortHash}_{dup:D3}.bak");
            }

            // need22-P0 S4：tmp + Move + fsync 原子写（失败不留半文件）
            AtomicWriteBytes(bakPath, bytes);

            var meta = new BackupMeta
            {
                Src = fullPath,
                Bytes = bytes.Length,
                Sha256 = hash,
                Tool = tool,
                At = DateTime.UtcNow.ToString("o"),
            };
            string metaPath = bakPath + ".meta.json";
            AtomicWriteText(metaPath, JsonSerializer.Serialize(meta));

            // need22-P0 S4：疑似密钥落盘审计（备份字节保持原样以保 undo 精确，仅告警）
            try
            {
                string asText = System.Text.Encoding.UTF8.GetString(bytes);
                if (SecretScrubber.ContainsSecret(asText))
                {
                    HaloLogger.App.Warn($"[Backup] 备份 '{name}' 疑似含密钥明文（已原样保存以保可恢复，仅审计告警）。");
                }
            }
            catch
            {
                // 二进制内容跳过文本扫描
            }

            AppendIndex(
                dir,
                new BackupIndexEntry
                {
                    Bak = Path.GetFileName(bakPath),
                    Src = fullPath,
                    Sha256 = hash,
                    Tool = tool,
                    At = meta.At,
                }
            );

            Prune(dir, KeepPerSession);
            return (bakPath, metaPath, hash);
        }
    }

    /// <summary>超限按 mtime 删最旧，只保留 keep 个 .bak（含配套 .meta.json + index 条目）。</summary>
    internal static void Prune(string sessionDir, int keep)
    {
        if (keep < 0)
        {
            keep = 0;
        }

        if (!Directory.Exists(sessionDir))
        {
            return;
        }

        string[] baks = Directory.GetFiles(sessionDir, "*.bak");
        if (baks.Length <= keep)
        {
            return;
        }

        // need22-P0 S4：按 mtime（非文件名字典序）删旧
        Array.Sort(
            baks,
            (a, b) =>
            {
                DateTime ta,
                    tb;
                try
                {
                    ta = File.GetLastWriteTimeUtc(a);
                }
                catch
                {
                    ta = DateTime.MinValue;
                }
                try
                {
                    tb = File.GetLastWriteTimeUtc(b);
                }
                catch
                {
                    tb = DateTime.MinValue;
                }
                int c = ta.CompareTo(tb);
                return c != 0 ? c : string.Compare(a, b, StringComparison.Ordinal);
            }
        );
        var doomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int remove = baks.Length - keep;
        for (int i = 0; i < remove; i++)
        {
            doomed.Add(baks[i]);
            try
            {
                File.Delete(baks[i]);
            }
            catch { }

            try
            {
                string meta = baks[i] + ".meta.json";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
            }
            catch { }
        }

        if (doomed.Count > 0)
        {
            RebuildIndexAfterPrune(sessionDir, doomed);
        }
    }

    /// <summary>need22-P0 S4：按 bak 文件名在 index.json 精确回查原始路径（替代 GuessOriginal）。</summary>
    internal static string? LookupOriginal(string sessionDir, string bakFileName)
    {
        try
        {
            string indexPath = Path.Combine(sessionDir, "index.json");
            if (!File.Exists(indexPath))
            {
                return null;
            }

            string want = Path.GetFileName(bakFileName);
            // 反序列化精确匹配（属性名大小写与写入端一致；文件名忽略大小写，最新条目优先）
            List<BackupIndexEntry>? list;
            try
            {
                list = JsonSerializer.Deserialize<List<BackupIndexEntry>>(
                    File.ReadAllText(indexPath)
                );
            }
            catch
            {
                return null;
            }
            if (list == null || list.Count == 0)
            {
                return null;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var el = list[i];
                if (el == null)
                {
                    continue;
                }
                if (
                    string.Equals(el.Bak, want, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(el.Src)
                )
                {
                    return el.Src;
                }
            }
        }
        catch
        {
            // 索引损坏返回 null，调用方回退 meta/显式 restore_to
        }

        return null;
    }

    internal static string SessionBackupDir(string dataRoot, string workspaceId, string sessionId)
    {
        string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
        return Path.Combine(
            root,
            "backups",
            string.IsNullOrWhiteSpace(workspaceId) ? "ws" : workspaceId,
            string.IsNullOrWhiteSpace(sessionId) ? "sess" : sessionId
        );
    }

    internal static long GetGlobalBackupBytes(string dataRoot)
    {
        try
        {
            string backupsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
                "backups"
            );
            if (!Directory.Exists(backupsRoot))
            {
                return 0;
            }

            long total = 0;
            foreach (
                string f in Directory.EnumerateFiles(
                    backupsRoot,
                    "*.bak",
                    SearchOption.AllDirectories
                )
            )
            {
                try
                {
                    total += new FileInfo(f).Length;
                }
                catch
                { /* 忽略竞态 */
                }
                if (total > GlobalQuotaBytes)
                {
                    break;
                }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static void AtomicWriteBytes(string finalPath, byte[] payload)
    {
        string? dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough
                )
            )
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 忽略残留临时文件清理异常
            }
        }
    }

    private static void AtomicWriteText(string finalPath, string content)
    {
        AtomicWriteBytes(finalPath, new System.Text.UTF8Encoding(false).GetBytes(content));
    }

    private static void AppendIndex(string sessionDir, BackupIndexEntry entry)
    {
        try
        {
            string indexPath = Path.Combine(sessionDir, "index.json");
            System.Collections.Generic.List<BackupIndexEntry> list = new();
            if (File.Exists(indexPath))
            {
                try
                {
                    var old =
                        JsonSerializer.Deserialize<System.Collections.Generic.List<BackupIndexEntry>>(
                            File.ReadAllText(indexPath)
                        );
                    if (old != null)
                    {
                        list = old;
                    }
                }
                catch
                {
                    // 索引损坏则重建
                }
            }

            list.Add(entry);
            // 索引上限 1000 条（防无限膨胀；超限丢最旧）
            while (list.Count > 1000)
            {
                list.RemoveAt(0);
            }

            AtomicWriteText(indexPath, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 索引失败不阻断备份主路径
        }
    }

    private static void RebuildIndexAfterPrune(string sessionDir, HashSet<string> removedFullPaths)
    {
        try
        {
            var removedNames = new HashSet<string>(
                removedFullPaths.Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase
            );
            string indexPath = Path.Combine(sessionDir, "index.json");
            if (!File.Exists(indexPath))
            {
                return;
            }

            System.Collections.Generic.List<BackupIndexEntry>? list;
            try
            {
                list =
                    JsonSerializer.Deserialize<System.Collections.Generic.List<BackupIndexEntry>>(
                        File.ReadAllText(indexPath)
                    );
            }
            catch
            {
                return;
            }

            if (list == null || list.Count == 0)
            {
                return;
            }

            int before = list.Count;
            list.RemoveAll(e => e != null && removedNames.Contains(e.Bak));
            if (list.Count != before)
            {
                AtomicWriteText(indexPath, JsonSerializer.Serialize(list));
            }
        }
        catch
        {
            // 索引重建失败不阻断 prune
        }
    }
}
