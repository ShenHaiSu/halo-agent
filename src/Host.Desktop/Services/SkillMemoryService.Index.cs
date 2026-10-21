#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed partial class SkillMemoryService
{
    // ---------- 索引 ----------

    private void EnsureIndexHealthyLocked(
        string domainRoot,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        string indexPath = Path.Combine(domainRoot, "_index.json");
        try
        {
            if (!File.Exists(indexPath))
            {
                RebuildIndexLocked(scope, kind, null, domainRoot);
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath, Utf8NoBom));
            if (
                !doc.RootElement.TryGetProperty("version", out var v)
                || v.GetInt32() != IndexVersion
            )
                throw new InvalidDataException($"_index.json version != {IndexVersion}");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[SkillMemory] _index.json 损坏/过时，已重建：{indexPath}", ex);
            try
            {
                RebuildIndexLocked(scope, kind, null, domainRoot);
            }
            catch (Exception ex2)
            {
                HaloLogger.App.Warn("[SkillMemory] 索引重建失败。", ex2);
            }
        }
    }

    private void RebuildIndexLocked(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot,
        string? knownRoot = null
    )
    {
        string? domainRoot = knownRoot ?? ResolveDomainRoot(scope, kind, workspaceRoot);
        if (domainRoot == null)
            return;
        Directory.CreateDirectory(domainRoot);

        var entries = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(domainRoot))
        {
            string rawName = Path.GetFileName(dir) ?? string.Empty;
            if (rawName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            string normalized;
            try
            {
                normalized = NormalizeAndValidateName(rawName);
            }
            catch
            {
                continue;
            }
            if (!seen.Add(normalized))
                continue;
            try
            {
                var e = TryLoadEntry(dir, normalized, scope, kind);
                if (e == null)
                    continue;
                string summary = e.Summary.Length <= 120 ? e.Summary : e.Summary[..120];
                entries.Add(
                    new Dictionary<string, object?>
                    {
                        ["name"] = e.Name,
                        ["scope"] = e.Scope,
                        ["enabled"] = e.Enabled,
                        ["pin"] = e.Pin,
                        ["priority"] = e.Priority,
                        ["version"] = e.Version,
                        ["title"] = e.Title,
                        ["summary"] = summary,
                        ["chars"] = e.BodyMarkdown.Length,
                        ["estTokens"] = e.EstTokens,
                        ["hash"] = e.Hash,
                        ["hasAssets"] = e.HasAssets,
                    }
                );
            }
            catch
            { /* 单条损坏跳过 */
            }
        }
        entries.Sort(
            (a, b) =>
                string.Compare((string)a["name"]!, (string)b["name"]!, StringComparison.Ordinal)
        );

        var index = new Dictionary<string, object?>
        {
            ["version"] = IndexVersion,
            ["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["scope"] = scope == SkillMemoryScope.Global ? "global" : "project",
            ["kind"] = kind == SkillMemoryKind.Skill ? "skill" : "memory",
        };
        if (kind == SkillMemoryKind.Skill)
            index["skills"] = entries;
        else
            index["memories"] = entries;

        WriteJsonAtomic(Path.Combine(domainRoot, "_index.json"), index);
    }

    private static void EnsureIndexFileLocked(
        string indexPath,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        try
        {
            string? dir = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            EnsureIndexFile(indexPath, scope, kind);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[SkillMemory] 索引初始化失败：{indexPath}", ex);
        }
    }

    private static void EnsureIndexFile(
        string indexPath,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        if (File.Exists(indexPath))
            return;
        var index = new Dictionary<string, object?>
        {
            ["version"] = IndexVersion,
            ["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["scope"] = scope == SkillMemoryScope.Global ? "global" : "project",
            ["kind"] = kind == SkillMemoryKind.Skill ? "skill" : "memory",
        };
        if (kind == SkillMemoryKind.Skill)
            index["skills"] = new List<object>();
        else
            index["memories"] = new List<object>();
        WriteJsonAtomic(indexPath, index);
    }

    private string BuildMergedSignature(string wsKey)
    {
        var sb = new StringBuilder(wsKey);
        sb.Append('|');
        foreach (
            var p in new[]
            {
                Path.Combine(_storage.GlobalSkillsDirectory, "_index.json"),
                Path.Combine(_storage.GlobalMemoryDirectory, "_index.json"),
            }
        )
        {
            sb.Append(SigOf(p)).Append(';');
        }
        if (!string.IsNullOrEmpty(wsKey))
        {
            try
            {
                string halo = ResolveProjectRoot(wsKey);
                sb.Append(SigOf(Path.Combine(halo, "skills", "_index.json"))).Append(';');
                sb.Append(SigOf(Path.Combine(halo, "memory", "_index.json"))).Append(';');
            }
            catch
            {
                sb.Append("bad;");
            }
        }
        return sb.ToString();
    }

    private static string SigOf(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "∅";
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
        }
        catch
        {
            return "?";
        }
    }

    private void InvalidateMergedCacheLocked()
    {
        _mergedCacheKey = null;
        _mergedCacheSig = null;
        _mergedCacheValue = null;
    }

    // ---------- 原子写盘 ----------

    private static void WriteTextAtomic(string filePath, string content)
    {
        string full = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                { /* 忽略残留 */
                }
            }
        }
    }

    private static void WriteBytesAtomic(string filePath, byte[] content)
    {
        string full = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                { /* 忽略残留 */
                }
            }
        }
    }

    private static void WriteJsonAtomic(string filePath, object data)
    {
        string json = JsonSerializer.Serialize(
            data,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }
        );
        WriteTextAtomic(filePath, json);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, Path.GetFullPath(file));
            string target = Path.Combine(dest, rel);
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
    }
}
