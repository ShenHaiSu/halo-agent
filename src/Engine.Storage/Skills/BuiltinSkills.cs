namespace HaloAgent.Engine.Storage.Skills;

/// <summary>
/// 内置技能目录维护：只保证目录存在，不覆盖用户内容。
/// 具体技能条目由 SkillScanner 统一扫描（builtin-skills 级）。
/// </summary>
public static class BuiltinSkills
{
    /// <summary>内置技能目录。</summary>
    public static string Dir(string dataDir) => Path.Combine(dataDir, "builtin-skills");

    /// <summary>确保内置技能目录存在（缺失才建，不碰已有文件）。</summary>
    public static void EnsureFiles(string dataDir)
    {
        try { Directory.CreateDirectory(Dir(dataDir)); } catch { }
    }

    /// <summary>列出内置级技能名。</summary>
    public static List<string> List(string dataDir)
    {
        var dir = Dir(dataDir);
        var names = new List<string>();
        if (!Directory.Exists(dir)) return names;
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var n = Path.GetFileName(d);
            if (SkillScanner.IsValidName(n) && File.Exists(Path.Combine(d, "SKILL.md")))
                names.Add(n);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
