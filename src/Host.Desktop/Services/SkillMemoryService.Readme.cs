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
    private sealed class FrontMeta
    {
        public int Version = 1;
        public bool Enabled = true;
        public int Priority = 100;
        public string Pin = "auto";
        public string? ScopeHint;
        public DateTime? UpdatedAtUtc;
    }

    private SkillMemoryEntry? TryLoadEntry(
        string dir,
        string normalized,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        string readmePath = Path.Combine(dir, "readme.md");
        if (!File.Exists(readmePath))
            return null; // E2：缺 readme → 跳过
        string raw = File.ReadAllText(readmePath, Utf8NoBom);
        var (front, bodyRaw) = ParseReadme(raw);
        string body = bodyRaw.Replace("\r\n", "\n");
        if (body.Length > MaxReadmeChars)
            return null; // 超限条目读路径跳过（写路径拒绝）

        // skill.json / memory.json 优先合并（缺键取 frontmatter，仍缺取默认）。
        int version = front.Version;
        bool enabled = front.Enabled;
        int priority = front.Priority;
        string pin = front.Pin;
        string? title = null;
        string? summary = front.ScopeHint;
        DateTime? updatedAt = front.UpdatedAtUtc;
        string metaFile = Path.Combine(
            dir,
            kind == SkillMemoryKind.Skill ? "skill.json" : "memory.json"
        );
        if (File.Exists(metaFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile, Utf8NoBom));
                var root = doc.RootElement;
                version = GetJsonInt(root, "version", version);
                enabled = GetJsonBool(root, "enabled", enabled);
                priority = GetJsonInt(root, "priority", priority);
                pin = GetJsonString(root, "pin", pin) ?? pin;
                title = GetJsonString(root, "title", title);
                summary =
                    GetJsonString(root, "summary", summary)
                    ?? GetJsonString(root, "scopeHint", summary)
                    ?? GetJsonString(root, "scope_hint", summary);
                string? ts =
                    GetJsonString(root, "updatedAtUtc", null)
                    ?? GetJsonString(root, "updated_at", null);
                if (!string.IsNullOrWhiteSpace(ts) && DateTime.TryParse(ts, out var parsed))
                    updatedAt = parsed.ToUniversalTime();
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn(
                    $"[SkillMemory] 条目 '{normalized}' 元数据 JSON 损坏，已用 frontmatter 兜底。",
                    ex
                );
            }
        }

        pin = NormalizePin(kind, pin);
        title = string.IsNullOrWhiteSpace(title) ? DeriveTitle(body) : title.Trim();
        summary = string.IsNullOrWhiteSpace(summary) ? DeriveSummary(body) : summary.Trim();
        string normalizedBody = body.TrimEnd();
        string hash = HeaderVariableResolver.ComputeDeterministicHash16(normalizedBody);
        string assetsDir = Path.Combine(dir, "assets");
        bool hasAssets =
            Directory.Exists(assetsDir)
            && Directory.EnumerateFiles(assetsDir, "*", SearchOption.AllDirectories).Any();
        DateTime mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(readmePath);
        }
        catch
        {
            mtime = DateTime.UtcNow;
        }

        return new SkillMemoryEntry(
            normalized,
            scope == SkillMemoryScope.Global ? "global" : "project",
            title,
            summary,
            body,
            pin,
            enabled,
            priority,
            version,
            (body.Length + 3) / 4,
            hash,
            hasAssets,
            Path.GetFullPath(dir),
            updatedAt ?? mtime
        ) with
        {
            Kind = kind
        };
    }

    private static (FrontMeta Meta, string Body) ParseReadme(string raw)
    {
        var meta = new FrontMeta();
        string text = (raw ?? string.Empty).Replace("\r\n", "\n").TrimStart('\uFEFF');
        if (
            !text.StartsWith("---\n", StringComparison.Ordinal)
            && !text.StartsWith("---\r\n", StringComparison.Ordinal)
        )
            return (meta, text);
        string[] lines = text.Split('\n');
        int end = -1;
        for (int i = 1; i < lines.Length && i < 60; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }
        if (end < 0)
            return (meta, text); // 无闭合则全文即正文
        for (int i = 1; i < end; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string val = Unquote(line[(colon + 1)..].Trim());
            switch (key)
            {
                case "version":
                    if (int.TryParse(val, out var v))
                        meta.Version = v;
                    break;
                case "enabled":
                    if (bool.TryParse(val, out var b))
                        meta.Enabled = b;
                    else if (val == "1" || val.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        meta.Enabled = true;
                    else if (val == "0" || val.Equals("no", StringComparison.OrdinalIgnoreCase))
                        meta.Enabled = false;
                    break;
                case "priority":
                    if (int.TryParse(val, out var p))
                        meta.Priority = p;
                    break;
                case "pin":
                    if (!string.IsNullOrWhiteSpace(val))
                        meta.Pin = val.ToLowerInvariant();
                    break;
                case "scope_hint":
                case "scopehint":
                    meta.ScopeHint = val;
                    break;
                case "updated_at":
                case "updatedatutc":
                    if (DateTime.TryParse(val, out var dt))
                        meta.UpdatedAtUtc = dt.ToUniversalTime();
                    break;
                default:
                    break; // 未知键保留但忽略
            }
        }
        return (meta, string.Join("\n", lines.Skip(end + 1)));
    }

    private static string Unquote(string v)
    {
        if (
            v.Length >= 2
            && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\'')))
        )
            return v[1..^1];
        return v;
    }

    private static string? GetJsonString(JsonElement root, string key, string? fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String
            )
                return prop.Value.GetString() ?? fallback;
        }
        return fallback;
    }

    private static int GetJsonInt(JsonElement root, string key, int fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out var v)
            )
                return v;
        }
        return fallback;
    }

    private static bool GetJsonBool(JsonElement root, string key, bool fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.Value.ValueKind == JsonValueKind.False)
                    return false;
            }
        }
        return fallback;
    }

    private static string NormalizePin(SkillMemoryKind kind, string? pin)
    {
        string p = (pin ?? "auto").Trim().ToLowerInvariant();
        if (kind == SkillMemoryKind.Skill)
            return (p == "full" || p == "auto") ? p : "auto";
        return (p == "pinned" || p == "archived" || p == "auto") ? p : "auto";
    }

    private static string DeriveTitle(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            string t = line.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrEmpty(t))
                return t.Length <= 80 ? t : t[..80];
        }
        return "Untitled";
    }

    private static string DeriveSummary(string body)
    {
        string flat = string.Join(
            " ",
            body.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l))
        );
        if (flat.StartsWith('#'))
            flat = flat.TrimStart('#').Trim();
        int dot = flat.IndexOfAny(new[] { '。', '！', '？', '!', '?', '.', ';', '；' });
        string first = dot >= 0 ? flat[..(dot + 1)].Trim() : flat.Trim();
        if (first.Length > 120)
            first = first[..120];
        return string.IsNullOrEmpty(first) ? "（空）" : first;
    }

    private static string BuildReadme(
        int version,
        bool enabled,
        int priority,
        string pin,
        string scopeHint,
        DateTime updatedAt,
        string body
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"version: {version}");
        sb.AppendLine($"enabled: {(enabled ? "true" : "false")}");
        sb.AppendLine($"priority: {priority}");
        sb.AppendLine($"pin: {pin}");
        sb.AppendLine($"scope_hint: \"{scopeHint.Replace("\"", "'")}\"");
        sb.AppendLine($"updated_at: \"{updatedAt:yyyy-MM-ddTHH:mm:ssZ}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body.TrimStart('\n'));
        if (!body.EndsWith('\n'))
            sb.AppendLine();
        return sb.ToString();
    }
}
