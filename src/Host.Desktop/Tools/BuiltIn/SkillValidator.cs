using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: Skill 校验/命名/配额（原 SkillToolShared 纯函数部分）。</summary>
public static class SkillValidator
{
    public const int MaxBodyChars = 8000;
    public const int MaxDomainChars = 60000;
    public const int MaxCountPerDomain = 200;
    public const int ListTruncateAt = 20;
    internal static readonly Regex NameRegex =
        new("^[a-z0-9][a-z0-9-_]{1,47}$", RegexOptions.Compiled);
    internal static readonly HashSet<string> ReservedNames =
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

    internal static string DefaultPin(bool isSkill) => isSkill ? "auto" : "pinned";

    internal static string? NormalizePin(bool isSkill, string? raw)
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

    internal static string PinError(bool isSkill) =>
        isSkill
            ? "pin 非法（skill 仅允许 auto|full）。[SKILL_VALIDATION]"
            : "pin 非法（memory 仅允许 pinned|archived|auto）。[SKILL_VALIDATION]";

    // ---------------- 读 ----------------

    internal static string ComputeSha16(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
