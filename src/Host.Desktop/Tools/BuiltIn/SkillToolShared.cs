using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

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

/// <summary>
/// need09-07：skill_tool / memory_tool 共享实现（双域存储 + 校验 + 索引 + 配额 + 事件）。
/// 路径（冻结，见 need09-01 §2）：Global &lt;DataRoot&gt;/skills|memory；Project &lt;WorkspaceRoot&gt;/.haloagent/skills|memory。
/// </summary>
public static class SkillToolShared
{
    public const int MaxBodyChars = 8000;
    public const int MaxDomainChars = 60000;
    public const int MaxCountPerDomain = 200;
    public const int ListTruncateAt = 20;

    private static readonly Regex NameRegex =
        new("^[a-z0-9][a-z0-9-_]{1,47}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "con",
            "prn",
            "aux",
            "nul",
            "com1",
            "com2",
            "com3",
            "com4",
            "com5",
            "com6",
            "com7",
            "com8",
            "com9",
            "lpt1",
            "lpt2",
            "lpt3",
            "lpt4",
            "lpt5",
            "lpt6",
            "lpt7",
            "lpt8",
            "lpt9",
        };
    private static readonly object _writeLock = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (DateTime At, object? Value)
    > _snapshot = new(StringComparer.Ordinal);

    public static int EstTokens(string? text) => ((text?.Length ?? 0) + 3) / 4;

    public static string NormalizeName(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>名称校验：null=通过；含 / \ .. 或保留名等返回错误文案（遍历类带 PATH_TRAVERSAL）。</summary>
    public static string? ValidateName(string? rawName)
    {
        string name = (rawName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return "缺少必需参数 'name'。请先用 action=list 确认名称后重试。[SKILL_VALIDATION]";
        if (
            name.Contains('/')
            || name.Contains('\\')
            || name == ".."
            || name.Contains("..")
            || Path.IsPathFullyQualified(name)
        )
            return $"非法名称 '{rawName}'：禁止路径分隔符、.. 与绝对路径（只能走 name 不能走路径）。[PATH_TRAVERSAL]";
        string lower = name.ToLowerInvariant();
        if (lower.StartsWith(".") || lower.StartsWith("_index") || lower.StartsWith("_tmp"))
            return $"非法名称 '{rawName}'：禁止以 . / _index / _tmp 开头。[SKILL_VALIDATION]";
        if (ReservedNames.Contains(lower))
            return $"非法名称 '{rawName}'：Windows 保留名不可用。[SKILL_VALIDATION]";
        if (!NameRegex.IsMatch(lower))
            return $"非法名称 '{rawName}'：必须匹配 ^[a-z0-9][a-z0-9-_]*（2-48 字符，首字符字母数字）。[SKILL_VALIDATION]";
        return null;
    }

    public static string ResolveScope(string? raw, ToolContext context, bool allowAll)
    {
        string s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
        {
            string d = (context.SkillMemoryScopeDefault ?? string.Empty).Trim().ToLowerInvariant();
            s = (d == "global" || d == "all") ? d : "project";
        }
        if (s == "all" && !allowAll)
            return "project";
        return s;
    }

    private static string KindDir(bool isSkill) => isSkill ? "skills" : "memory";

    private static string KindLabel(bool isSkill) => isSkill ? "skill" : "memory";

    private static string GlobalBase(string dataRoot, bool isSkill) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
            KindDir(isSkill)
        );

    private static string ProjectBase(string workspaceRoot, bool isSkill) =>
        Path.Combine(workspaceRoot, ".haloagent", KindDir(isSkill));

    private static bool HasWorkspace(ToolContext context) =>
        !string.IsNullOrWhiteSpace(context.WorkspaceRoot)
        && Directory.Exists(context.WorkspaceRoot);

    public static void InvalidateSnapshots() => _snapshot.Clear();

    private static void PublishChanged(bool isSkill, string scope, string name)
    {
        try
        {
            JsEventDispatcher.Publish(
                "config:skill-memory-changed",
                new
                {
                    kind = KindLabel(isSkill),
                    scope,
                    name
                }
            );
        }
        catch
        { /* ignore */
        }
    }

    private static string DefaultPin(bool isSkill) => isSkill ? "auto" : "pinned";

    private static string? NormalizePin(bool isSkill, string? raw)
    {
        string p = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(p))
            return DefaultPin(isSkill);
        if (isSkill)
            return p is "auto" or "full" ? p : null;
        if (p == "auto")
            return "pinned";
        return p is "pinned" or "archived" ? p : null;
    }

    private static string PinError(bool isSkill) =>
        isSkill
            ? "pin 非法（skill 仅允许 auto|full）。[SKILL_VALIDATION]"
            : "pin 非法（memory 仅允许 pinned|archived|auto）。[SKILL_VALIDATION]";

    // ---------------- 读 ----------------

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
        string n = NormalizeName(name);
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

    private static List<SkillMemoryEntry> ReadDomain(bool isSkill, string scope, string baseDir)
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

    private static SkillMemoryEntry? ReadOne(bool isSkill, string scope, string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        string name = NormalizeName(Path.GetFileName(dir));
        if (ValidateName(name) != null)
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
        string pin = NormalizePin(isSkill, Get("pin", DefaultPin(isSkill))) ?? DefaultPin(isSkill);
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
            EstTokens = EstTokens(normBody),
            Hash = ComputeSha16(normBody),
            HasAssets = hasAssets,
            DirPath = dir,
            UpdatedAtUtc = updated == default ? DateTime.UtcNow : updated,
        };
    }

    private static (Dictionary<string, string> Front, string Body) SplitFrontmatter(string raw)
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

    private static string? ExtractTitle(string body)
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

    private static string FirstSentence(string body)
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

    private static string ComputeSha16(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    private static void RebuildIndex(string baseDir, List<SkillMemoryEntry> entries, bool isSkill)
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

    // ---------------- 写 ----------------

    public sealed record WriteResult(
        bool Ok,
        string Message,
        SkillMemoryEntry? Entry,
        int DeltaTokens
    );

    public static WriteResult CreateOrUpdate(
        bool isSkill,
        string scope,
        string name,
        string? content,
        string? pin,
        int? priority,
        bool? enabled,
        ToolContext context,
        string dataRoot,
        bool isCreate
    )
    {
        string kind = KindLabel(isSkill);
        string? nameErr = ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project；list 可用 all）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !HasWorkspace(context))
            return new WriteResult(
                false,
                "当前无工作区（后台任务），project 域不可用；请用 scope=global 或打开工作区后重试。[SKILL_VALIDATION]",
                null,
                0
            );
        if (content != null && content.Length > MaxBodyChars)
            return new WriteResult(
                false,
                $"正文超限：{content.Length} 字符（上限 {MaxBodyChars}，约 {EstTokens(content)} tokens）。请截断到 {MaxBodyChars} 字符内后重试。[SKILL_VALIDATION]",
                null,
                0
            );
        string? normPin = pin == null ? null : NormalizePin(isSkill, pin);
        if (pin != null && normPin == null)
            return new WriteResult(false, PinError(isSkill), null, 0);
        if (priority.HasValue && (priority < 0 || priority > 1000))
            return new WriteResult(
                false,
                "priority 非法：允许 0..1000，默认 100。[SKILL_VALIDATION]",
                null,
                0
            );
        if (isCreate && string.IsNullOrWhiteSpace(content))
            return new WriteResult(
                false,
                $"create 需要 content（readme 正文，≤{MaxBodyChars} 字符）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (!isCreate && content == null && normPin == null && priority == null && enabled == null)
            return new WriteResult(
                false,
                "update 至少需要 content/pin/priority/enabled 其中之一。[SKILL_VALIDATION]",
                null,
                0
            );

        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? GlobalBase(dataRoot, isSkill)
                    : ProjectBase(context.WorkspaceRoot, isSkill);
            Directory.CreateDirectory(baseDir);
            var existing = ReadOne(isSkill, scope, Path.Combine(baseDir, n));
            if (!isCreate && existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}）：拒绝自动转 create（防拼写污染），请先 action=list 确认名称。[SKILL_VALIDATION]",
                    null,
                    0
                );

            string body = content ?? existing?.BodyMarkdown ?? string.Empty;
            string finalPin = normPin ?? existing?.Pin ?? DefaultPin(isSkill);
            int finalPriority = priority ?? existing?.Priority ?? 100;
            bool finalEnabled = enabled ?? existing?.Enabled ?? true;
            int finalVersion = (existing?.Version ?? 0) + 1;

            var domain = ReadDomain(isSkill, scope, baseDir);
            int used = domain.Where(e => e.Name != n).Sum(e => e.BodyMarkdown.Length);
            int others = domain.Count(e => e.Name != n);
            if (used + body.Length > MaxDomainChars)
            {
                var biggest = domain.OrderByDescending(e => e.BodyMarkdown.Length).FirstOrDefault();
                string hint =
                    biggest != null
                        ? $"建议删除 '{biggest.Name}'（约 {biggest.BodyMarkdown.Length} 字符，~{biggest.EstTokens} tokens）后再试。"
                        : "请删除部分条目后再试。";
                return new WriteResult(
                    false,
                    $"配额超限：{scope}-{KindDir(isSkill)} 已用 {used}/{MaxDomainChars} 字符，本次 +{body.Length} 将超限。"
                        + hint
                        + "[SKILL_VALIDATION]",
                    null,
                    0
                );
            }
            if (existing == null && others >= MaxCountPerDomain)
                return new WriteResult(
                    false,
                    $"数量超限：{scope}-{KindDir(isSkill)} 已有 {others}/{MaxCountPerDomain} 条，请先删除再新建。[SKILL_VALIDATION]",
                    null,
                    0
                );

            string dir = Path.Combine(baseDir, n);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            string readme = "# " + n + "\n\n" + body.Trim() + "\n";
            string front =
                "---\nversion: "
                + finalVersion
                + "\nenabled: "
                + (finalEnabled ? "true" : "false")
                + "\npriority: "
                + finalPriority
                + "\npin: "
                + finalPin
                + "\nscope_hint: \"\"\nupdated_at: \""
                + DateTime.UtcNow.ToString("o")
                + "\"\n---\n\n";
            WriteAtomic(Path.Combine(dir, "readme.md"), front + readme);
            WriteAtomic(
                Path.Combine(dir, isSkill ? "skill.json" : "memory.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        name = n,
                        version = finalVersion,
                        enabled = finalEnabled,
                        priority = finalPriority,
                        pin = finalPin,
                        updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );

            var entry = ReadOne(isSkill, scope, dir);
            RebuildIndex(baseDir, ReadDomain(isSkill, scope, baseDir), isSkill);
            InvalidateSnapshots();
            PublishChanged(isSkill, scope, n);

            int delta = (entry?.EstTokens ?? 0) - (existing?.EstTokens ?? 0);
            string deltaText = (delta >= 0 ? "+" : "") + delta + " tokens";
            string action = existing == null ? "已创建" : "已更新（覆盖保存）";
            string msg = $"{action} {kind} '{n}'（scope={scope}）。下轮生效；常驻索引 {deltaText}。";
            if (isSkill && finalPin == "full" && entry != null)
                msg += $"警告：pin=full 使正文每轮常驻（约 +{entry.EstTokens} tokens/轮），确认常驻请保留，否则改回 auto。";
            // need22-P0 S5：正文疑似密钥落盘审计（不阻断写入、不篡改正文，仅告警）
            try
            {
                if (!string.IsNullOrEmpty(body) && SecretScrubber.ContainsSecret(body))
                {
                    msg += "（注：正文疑似含密钥明文，已记录审计告警；请勿长期存放明文密钥。）";
                    HaloLogger.App.Warn($"[Skill] {kind} '{n}'（scope={scope}）正文疑似含密钥，已落盘并审计告警。");
                }
            }
            catch
            { /* 审计失败不阻断写入 */
            }
            return new WriteResult(true, msg, entry, delta);
        }
    }

    public static WriteResult Delete(
        bool isSkill,
        string scope,
        string name,
        ToolContext context,
        string dataRoot
    )
    {
        string kind = KindLabel(isSkill);
        string? nameErr = ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !HasWorkspace(context))
            return new WriteResult(false, "当前无工作区（后台任务），project 域不可用。[SKILL_VALIDATION]", null, 0);
        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? GlobalBase(dataRoot, isSkill)
                    : ProjectBase(context.WorkspaceRoot, isSkill);
            string dir = Path.Combine(baseDir, n);
            var existing = ReadOne(isSkill, scope, dir);
            if (existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}），无需删除；请先 action=list 确认。[SKILL_VALIDATION]",
                    null,
                    0
                );
            string bak = Path.Combine(
                baseDir,
                n + ".deleted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak"
            );
            try
            {
                if (Directory.Exists(bak))
                    Directory.Delete(bak, recursive: true);
                CopyDirectory(dir, bak);
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                return new WriteResult(false, $"删除失败：{ex.Message}。[SKILL_VALIDATION]", null, 0);
            }
            RebuildIndex(baseDir, ReadDomain(isSkill, scope, baseDir), isSkill);
            InvalidateSnapshots();
            PublishChanged(isSkill, scope, n);
            try
            {
                HaloLogger.App.Warn($"[Skill] 已删除{scope} {kind} '{n}'（备份 {bak}）");
            }
            catch { }
            return new WriteResult(
                true,
                $"已删除{scope} {kind} '{n}'，不可撤销（仅留一份备份 {Path.GetFileName(bak)}）。下轮生效；常驻索引 -{existing.EstTokens} tokens。",
                null,
                -existing.EstTokens
            );
        }
    }

    public static WriteResult SetEnabled(
        bool isSkill,
        string scope,
        string name,
        bool enable,
        ToolContext context,
        string dataRoot
    )
    {
        string kind = KindLabel(isSkill);
        string? nameErr = ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !HasWorkspace(context))
            return new WriteResult(false, "当前无工作区（后台任务），project 域不可用。[SKILL_VALIDATION]", null, 0);
        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? GlobalBase(dataRoot, isSkill)
                    : ProjectBase(context.WorkspaceRoot, isSkill);
            var existing = ReadOne(isSkill, scope, Path.Combine(baseDir, n));
            if (existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}）；请先 action=list 确认。[SKILL_VALIDATION]",
                    null,
                    0
                );
            return
                CreateOrUpdate(
                    isSkill,
                    scope,
                    n,
                    content: null,
                    pin: null,
                    priority: null,
                    enabled: enable,
                    context,
                    dataRoot,
                    isCreate: false
                )
                    is WriteResult r
                && r.Ok
                ? new WriteResult(
                    true,
                    $"已{(enable ? "启用" : "停用")} {kind} '{n}'（scope={scope}）。下轮生效。",
                    r.Entry,
                    0
                )
                : new WriteResult(false, $"开关切换失败：{kind} '{n}'。[SKILL_VALIDATION]", null, 0);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ---------------- 工具入口 ----------------

    public static async Task<ToolResult> ExecuteKindAsync(
        bool isSkill,
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string kind = KindLabel(isSkill);
            string action = "list",
                scope = string.Empty,
                name = string.Empty,
                content = string.Empty;
            bool hasContent = false,
                hasScope = false,
                hasName = false;
            string? pin = null;
            int? priority = null;
            bool? enabled = null;
            try
            {
                // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口）
                using var toolArgs = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = toolArgs.Root;
                if (root.ValueKind != JsonValueKind.Object)
                    return ToolResult.Fail(
                        $"参数必须是对象。正确示例：{{\"action\":\"list\",\"scope\":\"project\"}}。[SKILL_VALIDATION]",
                        durationMs: sw.ElapsedMilliseconds
                    );
                string? a = toolArgs.GetString("action");
                if (!string.IsNullOrWhiteSpace(a))
                    action = a.Trim().ToLowerInvariant();
                scope = toolArgs.GetString("scope") ?? string.Empty;
                hasScope = toolArgs.Root.TryGetProperty("scope", out _);
                name = toolArgs.GetString("name") ?? string.Empty;
                hasName = toolArgs.Root.TryGetProperty("name", out _);
                string? c = toolArgs.GetString("content");
                if (c != null || toolArgs.Root.TryGetProperty("content", out _))
                {
                    content = c ?? string.Empty;
                    hasContent =
                        toolArgs.Root.TryGetProperty("content", out var cc)
                        && cc.ValueKind == JsonValueKind.String;
                }
                // memory 存量别名：body 与 content 二选一（validator 已做条件必填，handler 双认）
                if (!hasContent && toolArgs.GetString("body") is string b)
                {
                    content = b;
                    hasContent = true;
                }
                pin = toolArgs.GetString("pin");
                if (toolArgs.TryGetInt32("priority", out int pv))
                    priority = pv;
                if (toolArgs.TryGetBool("enabled", out bool eb))
                    enabled = eb;
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。正确示例：{{\"action\":\"list\",\"scope\":\"project\"}}。[SKILL_VALIDATION]",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot)
                ? Path.GetTempPath()
                : context.DataRoot;
            bool allowAll = action == "list";
            string scopeResolved = ResolveScope(hasScope ? scope : null, context, allowAll);
            if (scopeResolved is not ("global" or "project" or "all"))
                return ToolResult.Fail(
                    $"scope 非法：'{scope}'（仅允许 global|project{(allowAll ? "|all" : string.Empty)}）。[SKILL_VALIDATION]",
                    durationMs: sw.ElapsedMilliseconds
                );
            if (!allowAll && scopeResolved == "all")
                scopeResolved = "project";

            // need13：子写 skill/memory 鉴权（读 list/get 放行；写 create/update/delete/enable/disable 按快照拦截）
            bool isSubWrite =
                !string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "get", StringComparison.OrdinalIgnoreCase);
            if (
                isSubWrite
                && string.Equals(context.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
            )
            {
                bool allowed = isSkill ? context.SubAllowSkillWrite : context.SubAllowMemoryWrite;
                if (!allowed)
                    return ToolResult.Fail(
                        $"[PERMISSION_DENIED:{(isSkill ? "skill_tool" : "memory_tool")}:write] 子代理禁止写 {(isSkill ? "skill" : "memory")}（设置>智能体行为>子代理>{(isSkill ? "skill" : "memory")}写=false）。list/get 可用，本次 {action} 已拒绝。请改用只读查询，或记入总结交主代理修改。",
                        durationMs: sw.ElapsedMilliseconds
                    );
                // 子写 global 域同样拒绝（即使开关开了，见 03 §3.3）
                if (string.Equals(scopeResolved, "global", StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Fail(
                        $"[PERMISSION_DENIED:{(isSkill ? "skill_tool" : "memory_tool")}:write] 子代理禁止写全局 {(isSkill ? "skill" : "memory")}（scope=global）。本次 {action} 已拒绝。请改用 project 域或记入总结交主代理修改。",
                        durationMs: sw.ElapsedMilliseconds
                    );
            }

            switch (action)
            {
                case "list":
                {
                    var entries = ListEntries(isSkill, scopeResolved, context, dataRoot);
                    string note =
                        (scopeResolved == "all" && !HasWorkspace(context))
                            ? "（Project 域：(no workspace) 为空，只返 Global）"
                            : string.Empty;
                    return ToolResult.Ok(
                        FormatList(isSkill, entries, note),
                        durationMs: sw.ElapsedMilliseconds
                    );
                }
                case "get":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"get 需要 name。请先 action=list 确认名称。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    string? nameErr = ValidateName(name);
                    if (nameErr != null)
                        return ToolResult.Fail(nameErr, durationMs: sw.ElapsedMilliseconds);
                    var e = GetEntry(isSkill, scopeResolved, name, context, dataRoot);
                    if (e == null)
                        return ToolResult.Fail(
                            $"不存在的 {kind} '{NormalizeName(name)}'（scope={scopeResolved}）。请先 action=list 确认。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return ToolResult.Ok(FormatGet(e), durationMs: sw.ElapsedMilliseconds);
                }
                case "create":
                    return FromWrite(
                        CreateOrUpdate(
                            isSkill,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: true
                        ),
                        sw
                    );
                case "set" when !isSkill:
                {
                    // need22-P0 S3：memory 存量 set 别名 = upsert（存在则 update，不存在则 create；与 validator 对齐）
                    bool exists =
                        !string.IsNullOrWhiteSpace(name)
                        && GetEntry(false, scopeResolved, name, context, dataRoot) != null;
                    return FromWrite(
                        CreateOrUpdate(
                            false,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: !exists
                        ),
                        sw
                    );
                }
                case "update":
                    return FromWrite(
                        CreateOrUpdate(
                            isSkill,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: false
                        ),
                        sw
                    );
                case "delete":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"delete 需要 name。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return FromWrite(Delete(isSkill, scopeResolved, name, context, dataRoot), sw);
                }
                case "enable":
                case "disable":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"{action} 需要 name。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    if (hasContent)
                        return ToolResult.Fail(
                            $"{action} 不接受 content（改正文请用 update）。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return FromWrite(
                        SetEnabled(
                            isSkill,
                            scopeResolved,
                            name,
                            action == "enable",
                            context,
                            dataRoot
                        ),
                        sw
                    );
                }
                default:
                    return ToolResult.Fail(
                        $"未知 action '{action}'（仅允许 list|get|create|update|delete|enable|disable，默认 list）。[SKILL_VALIDATION]",
                        durationMs: sw.ElapsedMilliseconds
                    );
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("操作已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                $"执行异常：{ex.Message}。请修正参数后重试。[SKILL_VALIDATION]",
                durationMs: sw.ElapsedMilliseconds
            );
        }
        finally
        {
            sw.Stop();
        }
    }

    private static ToolResult FromWrite(WriteResult r, Stopwatch sw) =>
        r.Ok
            ? ToolResult.Ok(r.Message, durationMs: sw.ElapsedMilliseconds)
            : ToolResult.Fail(r.Message, durationMs: sw.ElapsedMilliseconds);

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
            if (shown >= ListTruncateAt)
                break;
            sb.AppendLine(
                $"| {e.Scope} | {e.Name} | {e.Pin} | {(e.Enabled ? "on" : "off")} | ~{e.EstTokens} |"
            );
            shown++;
        }
        if (entries.Count > ListTruncateAt)
            sb.AppendLine($"…还有 {entries.Count - ListTruncateAt} 条未显示，用 get 查详情。");
        return sb.ToString().TrimEnd();
    }

    internal static string FormatGet(SkillMemoryEntry e) =>
        $"[{e.Scope}] {e.Name}（pin={e.Pin}，enabled={(e.Enabled ? "true" : "false")}，priority={e.Priority}，version={e.Version}，estTokens≈{e.EstTokens}）\n---\n{e.BodyMarkdown}";
}
