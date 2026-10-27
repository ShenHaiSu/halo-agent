namespace HaloAgent.Engine.Storage.Personas;

/// <summary>
/// 用户人格文件存储（dataDir/personas/*.md）。
/// 内置人格的文案在 Engine.Core 的 PersonaBuiltin；这里只管用户目录的读写。
/// </summary>
public static class PersonaStore
{
    /// <summary>人格目录。</summary>
    public static string Dir(string dataDir) => Path.Combine(dataDir, "personas");

    /// <summary>列出用户目录中的人格名（不含内置）。</summary>
    public static List<string> List(string dataDir)
    {
        var dir = Dir(dataDir);
        var names = new List<string>();
        if (!Directory.Exists(dir)) return names;
        foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
        {
            var n = Path.GetFileNameWithoutExtension(f);
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>读取用户人格正文；不存在返回 null。</summary>
    public static string? Get(string dataDir, string name)
    {
        if (!IsValidName(name)) return null;
        var path = Path.Combine(Dir(dataDir), name + ".md");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    /// <summary>保存用户人格（覆盖写）。</summary>
    public static void Save(string dataDir, string name, string text)
    {
        if (!IsValidName(name)) throw new ArgumentException("非法的人格名: " + name, nameof(name));
        Directory.CreateDirectory(Dir(dataDir));
        File.WriteAllText(Path.Combine(Dir(dataDir), name + ".md"), text);
    }

    /// <summary>删除用户人格；不存在返回 false。</summary>
    public static bool Delete(string dataDir, string name)
    {
        if (!IsValidName(name)) return false;
        var path = Path.Combine(Dir(dataDir), name + ".md");
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name == "." || name == "..") return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        return true;
    }
}
