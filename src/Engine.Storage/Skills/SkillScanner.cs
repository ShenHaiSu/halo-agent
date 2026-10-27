namespace HaloAgent.Engine.Storage.Skills;

public enum SkillSource { Builtin, User, Project }

public sealed record Skill
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool DisableModelInvocation { get; init; }
    public string? Entry { get; init; }
    public string Body { get; init; } = "";
    public string Dir { get; init; } = "";
    public SkillSource Source { get; init; }

    public string Render(string args) => Body.Replace("{args}", args);
}

public sealed record SkillSet(List<Skill> Skills, List<string> Warnings);

/// <summary>
/// 技能扫描（三层）：内置 &lt; builtin-skills &lt; 用户 skills &lt; 项目 .haloagent/skills。
/// 同名时：项目 &gt; 用户 &gt; 内置。
/// name 必须是字母数字下划线连字符、≤40 字符、不以 - 开头，且与目录名一致；
/// entry 必须是技能目录内的相对路径，不能含 ..。不满足则跳过并记 warning。
/// </summary>
public static class SkillScanner
{
    public static SkillSet Scan(string cwd, string dataDir)
    {
        var map = new SortedDictionary<string, Skill>(StringComparer.Ordinal);
        var warnings = new List<string>();

        ScanDir(Path.Combine(dataDir, "builtin-skills"), SkillSource.Builtin, map, warnings);
        ScanDir(Path.Combine(dataDir, "skills"), SkillSource.User, map, warnings);
        ScanDir(Path.Combine(cwd, ".haloagent", "skills"), SkillSource.Project, map, warnings);

        return new SkillSet(map.Values.ToList(), warnings);
    }

    private static void ScanDir(
        string dir, SkillSource source,
        SortedDictionary<string, Skill> map, List<string> warnings)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var skillDir in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(skillDir);
            if (!IsValidName(name))
            {
                warnings.Add("跳过无效技能目录名: " + name + "（" + SourceLabel(source) + " 级）");
                continue;
            }

            var mdPath = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(mdPath))
            {
                warnings.Add("跳过 " + skillDir + "（无 SKILL.md）");
                continue;
            }

            try
            {
                var text = File.ReadAllText(mdPath);
                var (fm, body) = ParseFrontmatter(text);

                if (fm.Name is not null && fm.Name != name)
                {
                    warnings.Add("跳过 " + mdPath + ": frontmatter name 为「" + fm.Name + "」，与目录名「" + name + "」不一致");
                    continue;
                }

                // entry 必须是目录内相对路径
                string? entry = null;
                if (fm.Entry is not null)
                {
                    var e = fm.Entry;
                    if (Path.IsPathRooted(e) || e.Split('/', '\\').Contains(".."))
                    {
                        warnings.Add("跳过 " + mdPath + ": entry 必须是技能目录内的相对路径，不能含 ..");
                        continue;
                    }
                    entry = e;
                }

                map[name] = new Skill
                {
                    Name = name,
                    Description = fm.Description,
                    DisableModelInvocation = fm.DisableModelInvocation,
                    Entry = entry,
                    Body = body,
                    Dir = skillDir,
                    Source = source,
                };
            }
            catch (Exception ex)
            {
                warnings.Add("读取 " + mdPath + " 失败: " + ex.Message);
            }
        }
    }

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 40) return false;
        if (name[0] == '-') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }

    private sealed class Frontmatter
    {
        public string? Name;
        public string Description = "";
        public bool DisableModelInvocation;
        public string? Entry;
    }

    /// <summary>frontmatter 简子集（不引 YAML 库）。</summary>
    private static (Frontmatter fm, string body) ParseFrontmatter(string text)
    {
        var fm = new Frontmatter();
        if (!text.StartsWith("---")) return (fm, text.Trim());

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (fm, text.Trim());

        var header = text[3..end];
        var body = text[(end + 4)..].Trim();

        foreach (var line in header.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("name:", StringComparison.Ordinal))
                fm.Name = l[5..].Trim().Trim('"', '\'');
            else if (l.StartsWith("description:", StringComparison.Ordinal))
                fm.Description = l[12..].Trim().Trim('"', '\'');
            else if (l.StartsWith("disable-model-invocation:", StringComparison.Ordinal))
                fm.DisableModelInvocation = l[26..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            else if (l.StartsWith("entry:", StringComparison.Ordinal))
                fm.Entry = l[6..].Trim().Trim('"', '\'');
        }
        return (fm, body);
    }

    private static string SourceLabel(SkillSource s) => s switch
    {
        SkillSource.Builtin => "内置",
        SkillSource.User => "用户",
        SkillSource.Project => "项目",
        _ => "?",
    };

    /// <summary>构造入口脚本命令（跨平台）。</summary>
    public static string? BuildEntryCommand(Skill skill, string args)
    {
        if (skill.Entry is null) return null;

        var candidates = OperatingSystem.IsWindows()
            ? WinEntryCandidates(skill.Entry)
            : new List<string> { skill.Entry };

        foreach (var cand in candidates)
        {
            var full = Path.Combine(skill.Dir, cand.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            var lower = cand.ToLowerInvariant();

            if (OperatingSystem.IsWindows())
            {
                if (lower.EndsWith(".ps1"))
                    return "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + full + "\" \"" + args + "\"";
                if (lower.EndsWith(".bat") || lower.EndsWith(".cmd"))
                    return "\"" + full + "\" \"" + args + "\"";
                return "\"" + full + "\" \"" + args + "\"";
            }

            // POSIX：用 sh 跑（不依赖 +x / shebang）
            return "/bin/sh \"" + full + "\" \"" + args + "\"";
        }

        return null;
    }

    private static List<string> WinEntryCandidates(string declared)
    {
        var ext = Path.GetExtension(declared).ToLowerInvariant();
        if (ext is ".ps1" or ".bat" or ".cmd") return new List<string> { declared };

        var dir = Path.GetDirectoryName(declared)?.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(declared);

        var result = new List<string>();
        foreach (var e in new[] { ".ps1", ".bat", ".cmd" })
            result.Add(string.IsNullOrEmpty(dir) ? stem + e : dir + "/" + stem + e);
        result.Add(declared);
        return result;
    }
}
