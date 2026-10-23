using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: Skill/Memory 文件读取/索引（原 SkillToolShared 读路径）。</summary>
public static class SkillFileReader
{
    /// <summary>need09-07：Skill/Memory 条目（读合并：同名 Project 覆盖 Global）。</summary>
    public sealed class SkillMemoryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string BodyMarkdown { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public int Version { get; set; } = 1;
        public int EstTokens { get; set; }
        public int Chars { get; set; }
        public string Hash { get; set; } = string.Empty;
        public bool HasAssets { get; set; }
        public string DirPath { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    internal static string KindDir(bool isSkill) => isSkill ? "skills" : "memory";

    internal static string KindLabel(bool isSkill) => isSkill ? "skill" : "memory";

    internal static string GlobalBase(string dataRoot, bool isSkill) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
            KindDir(isSkill)
        );

    internal static string ProjectBase(string workspaceRoot, bool isSkill) =>
        Path.Combine(workspaceRoot, ".haloagent", KindDir(isSkill));

    internal static bool HasWorkspace(ToolContext context) =>
        !string.IsNullOrWhiteSpace(context.WorkspaceRoot)
        && Directory.Exists(context.WorkspaceRoot);

    public static List<SkillMemoryEntry> ListEntries(
        bool isSkill,
        string scope,
        ToolContext context,
        string dataRoot
    )
    {
        var result = new List<SkillMemoryEntry>();
        bool wantGlobal = scope is "global" or "all";
        bool wantProject = scope is "project" or "all";
        if (wantGlobal)
            result.AddRange(ReadDomain(isSkill, "global", GlobalBase(dataRoot, isSkill)));
        if (wantProject)
        {
            if (HasWorkspace(context))
                result.AddRange(
                    ReadDomain(isSkill, "project", ProjectBase(context.WorkspaceRoot, isSkill))
                );
        }
        result.Sort(
            (a, b) =>
            {
                int c = b.Priority.CompareTo(a.Priority);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            }
        );
        return result;
    }

    public static SkillMemoryEntry? GetEntry(
        bool isSkill,
        string scope,
        string name,
        ToolContext context,
        string dataRoot
    )
    {
        string n = SkillValidator.NormalizeName(name);
        if (scope is "project" or "all")
        {
            if (HasWorkspace(context))
            {
                var hit = ReadOne(
                    isSkill,
                    "project",
                    Path.Combine(ProjectBase(context.WorkspaceRoot, isSkill), n)
                );
                if (hit != null)
                    return hit;
                if (scope == "project")
                    return null;
            }
            else if (scope == "project")
                return null;
        }
        if (scope is "global" or "all")
            return ReadOne(isSkill, "global", Path.Combine(GlobalBase(dataRoot, isSkill), n));
        return null;
    }

    internal static List<SkillMemoryEntry> ReadDomain(bool isSkill, string scope, string baseDir)
    {
        var list = new List<SkillMemoryEntry>();
        try
        {
            if (!Directory.Exists(baseDir))
                return list;
            foreach (string dir in Directory.GetDirectories(baseDir))
            {
                string dn = Path.GetFileName(dir);
                if (
                    dn.StartsWith("_")
                    || dn.StartsWith(".")
                    || dn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                try
                {
                    var e = ReadOne(isSkill, scope, dir);
                    if (e != null)
                        list.Add(e);
                }
                catch (Exception ex)
                {
                    try
                    {
                        HaloLogger.App.Warn($"[Skill] 跳过损坏条目 '{dir}'：{ex.Message}");
                    }
                    catch { }
                }
            }
            RebuildIndex(baseDir, list, isSkill);
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Warn($"[Skill] 读取域失败 '{baseDir}'：{ex.Message}");
            }
            catch { }
        }
        return list;
    }

    internal static SkillMemoryEntry? ReadOne(bool isSkill, string scope, string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        string name = SkillValidator.NormalizeName(Path.GetFileName(dir));
        if (SkillValidator.ValidateName(name) != null)
            return null;
        string readme = Path.Combine(dir, "readme.md");
        if (!File.Exists(readme))
            return null;
        string raw = File.ReadAllText(readme, Encoding.UTF8);
        var (front, body) = SplitFrontmatter(raw);
        string metaFile = Path.Combine(dir, isSkill ? "skill.json" : "memory.json");
        Dictionary<string, string> meta = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(metaFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile, Encoding.UTF8));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        meta[p.Name] = p.Value.ToString();
            }
        }
        catch
        { /* json 损坏则回退 frontmatter */
        }

        string Get(string k, string fb)
        {
            if (meta.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
            if (front.TryGetValue(k, out var f) && !string.IsNullOrWhiteSpace(f))
                return f.Trim();
            return fb;
        }

        bool enabled =
            Get("enabled", "true").Trim().ToLowerInvariant()
                is not ("false" or "0" or "no" or "off");
        int.TryParse(Get("priority", "100"), out int priority);
        priority = Math.Clamp(priority, 0, 1000);
        int.TryParse(Get("version", "1"), out int version);
        if (version < 1)
            version = 1;
        string pin =
            SkillValidator.NormalizePin(isSkill, Get("pin", SkillValidator.DefaultPin(isSkill)))
            ?? SkillValidator.DefaultPin(isSkill);
        string scopeHint = Get("scope_hint", string.Empty);
        DateTime.TryParse(Get("updated_at", string.Empty), out DateTime updated);

        string title = ExtractTitle(body) ?? name;
        string summary = !string.IsNullOrWhiteSpace(scopeHint)
            ? scopeHint.Trim()
            : FirstSentence(body);
        if (summary.Length > 120)
            summary = summary.Substring(0, 120);
        string normBody = (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd();
        bool hasAssets =
            Directory.Exists(Path.Combine(dir, "assets"))
            && Directory.GetFiles(Path.Combine(dir, "assets")).Length > 0;
        return new SkillMemoryEntry
        {
            Name = name,
            Scope = scope,
            Title = title,
            Summary = summary,
            BodyMarkdown = normBody,
            Pin = pin,
            Enabled = enabled,
            Priority = priority,
            Version = version,
            Chars = normBody.Length,
            EstTokens = SkillValidator.EstTokens(normBody),
            Hash = SkillValidator.ComputeSha16(normBody),
            HasAssets = hasAssets,
            DirPath = dir,
            UpdatedAtUtc = updated == default ? DateTime.UtcNow : updated,
        };
    }

    internal static (Dictionary<string, string> Front, string Body) SplitFrontmatter(string raw)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = raw ?? string.Empty;
        if (!text.StartsWith("---"))
            return (front, text);
        int end = text.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
            return (front, text);
        string head = text.Substring(3, end - 3);
        string body = text.Substring(end + 4).TrimStart('\r', '\n');
        foreach (string line in head.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#"))
                continue;
            int c = t.IndexOf(':');
            if (c <= 0)
                continue;
            front[t.Substring(0, c).Trim()] = t.Substring(c + 1).Trim().Trim('"', '\'');
        }
        return (front, body);
    }

    internal static string? ExtractTitle(string body)
    {
        foreach (string line in (body ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("# "))
                return t.Substring(2).Trim();
            if (!string.IsNullOrWhiteSpace(t))
                return t.Length > 60 ? t.Substring(0, 60) : t;
        }
        return null;
    }

    internal static string FirstSentence(string body)
    {
        string t = (body ?? string.Empty).Replace("\r\n", "\n").Trim();
        foreach (string line in t.Split('\n'))
        {
            string s = line.Trim().TrimStart('#', ' ', '>', '-', '*');
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return string.Empty;
    }

    internal static void RebuildIndex(string baseDir, List<SkillMemoryEntry> entries, bool isSkill)
    {
        try
        {
            var rows = entries
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new
                {
                    name = e.Name,
                    scope = e.Scope,
                    enabled = e.Enabled,
                    pin = e.Pin,
                    priority = e.Priority,
                    version = e.Version,
                    title = e.Title,
                    summary = e.Summary,
                    chars = e.Chars,
                    estTokens = e.EstTokens,
                    hash = e.Hash,
                    hasAssets = e.HasAssets,
                })
                .ToList();
            object doc = isSkill
                ? new
                {
                    version = 1,
                    updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    skills = rows
                }
                : new
                {
                    version = 1,
                    updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    memories = rows
                };
            string tmp = Path.Combine(
                baseDir,
                "_index.json." + Guid.NewGuid().ToString("N") + ".tmp"
            );
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8
            );
            File.Move(tmp, Path.Combine(baseDir, "_index.json"), overwrite: true);
        }
        catch
        { /* 索引重建永不阻断读写 */
        }
    }

    internal static string FormatList(bool isSkill, List<SkillMemoryEntry> entries, string note)
    {
        string kind = KindLabel(isSkill);
        if (entries.Count == 0)
            return $"{kind} 清单（0 条）{note}：暂无条目。用 action=create 新建（默认写 project 域）。";
        var sb = new StringBuilder();
        sb.AppendLine($"{kind} 清单（{entries.Count} 条）{note}：");
        sb.AppendLine("| scope | name | pin | enabled | estTokens |");
        sb.AppendLine("|---|---|---|---|---|");
        int shown = 0;
        foreach (var e in entries)
        {
            if (shown >= SkillValidator.ListTruncateAt)
                break;
            sb.AppendLine(
                $"| {e.Scope} | {e.Name} | {e.Pin} | {(e.Enabled ? "on" : "off")} | ~{e.EstTokens} |"
            );
            shown++;
        }
        if (entries.Count > SkillValidator.ListTruncateAt)
            sb.AppendLine($"…还有 {entries.Count - SkillValidator.ListTruncateAt} 条未显示，用 get 查详情。");
        return sb.ToString().TrimEnd();
    }

    internal static string FormatGet(SkillMemoryEntry e) =>
        $"[{e.Scope}] {e.Name}（pin={e.Pin}，enabled={(e.Enabled ? "true" : "false")}，priority={e.Priority}，version={e.Version}，estTokens≈{e.EstTokens}）\n---\n{e.BodyMarkdown}";
}
