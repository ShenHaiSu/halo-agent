using System.Text.Json;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need15 P2-2：备份存储（命名/prune/meta/恢复）。
/// 路径：&lt;DataRoot&gt;/backups/&lt;workspaceId&gt;/&lt;sessionId&gt;/yyyyMMdd-HHmmss-fff_&lt;guid8&gt;_&lt;basename&gt;.bak + 同名 .meta.json
/// P0-2 先行复用：WriteBackup + Prune。
/// </summary>
internal static class BackupStore
{
    /// <summary>每会话最多保留备份数（单测可调，线上热调参考 AskUserGate 模式）。</summary>
    internal static int KeepPerSession = 50;

    internal sealed class BackupMeta
    {
        public string Src { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string? Tool { get; set; }
        public string At { get; set; } = string.Empty;
    }

    /// <summary>落盘前备份原文件。返回 (bak绝对路径, meta绝对路径, 原文件hash)。文件不存在则返回 (null,null,null)。</summary>
    internal static (string? BakPath, string? MetaPath, string? Hash) WriteBackup(
        string fullPath, string dataRoot, string workspaceId, string sessionId, string? tool = null)
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
        string baseName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "file";
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string name = $"{stamp}_{Guid.NewGuid():N}".Substring(0, stamp.Length + 9) + "_" + baseName + ".bak";
        // 上行 Substring 保证 guid 取 8 位：yyyyMMdd-HHmmss-fff (19) + _ + 8 = 28
        string bakPath = Path.Combine(dir, name);
        File.Copy(fullPath, bakPath, overwrite: false);
        var meta = new BackupMeta
        {
            Src = fullPath,
            Bytes = bytes.Length,
            Sha256 = hash,
            Tool = tool,
            At = DateTime.UtcNow.ToString("o"),
        };
        string metaPath = bakPath + ".meta.json";
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
        Prune(dir, KeepPerSession);
        return (bakPath, metaPath, hash);
    }

    /// <summary>超限按时间删最旧，只保留 keep 个 .bak（含配套 .meta.json）。</summary>
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

        Array.Sort(baks, StringComparer.Ordinal);
        int remove = baks.Length - keep;
        for (int i = 0; i < remove; i++)
        {
            try
            {
                File.Delete(baks[i]);
            }
            catch
            {
            }

            try
            {
                string meta = baks[i] + ".meta.json";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
            }
            catch
            {
            }
        }
    }

    internal static string SessionBackupDir(string dataRoot, string workspaceId, string sessionId)
    {
        string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
        return Path.Combine(root, "backups",
            string.IsNullOrWhiteSpace(workspaceId) ? "ws" : workspaceId,
            string.IsNullOrWhiteSpace(sessionId) ? "sess" : sessionId);
    }
}
