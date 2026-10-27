namespace HaloAgent.Engine.Storage;

/// <summary>
/// 数据目录的唯一来源。
///
/// 铁律：所有路径都从这里取，不在别处拼。
/// （以前"日志目录"和"数据目录"分别拼过，结果两边不一致，日志写到了别的地方）
/// </summary>
public static class Paths
{
    public const string AppFolderName = ".haloagent";

    /// <summary>数据目录：%USERPROFILE%\.haloagent（HALO_DATA_DIR 可覆盖，测试用）。</summary>
    public static string DataDir
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Path.GetTempPath();
            return Path.Combine(home, AppFolderName);
        }
    }

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string McpFile => Path.Combine(DataDir, "mcp.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string SessionsDir => Path.Combine(DataDir, "sessions");
    public static string MemoriesDir => Path.Combine(DataDir, "memories");
    public static string SkillsDir => Path.Combine(DataDir, "skills");
    public static string BuiltinSkillsDir => Path.Combine(DataDir, "builtin-skills");
    public static string PersonasDir => Path.Combine(DataDir, "personas");
    public static string ThemesDir => Path.Combine(DataDir, "themes");
    public static string UndoDir => Path.Combine(DataDir, "undo");
    public static string UndoManifest => Path.Combine(UndoDir, "manifest.jsonl");
    public static string UndoFilesDir => Path.Combine(UndoDir, "files");
    public static string BlobsDir => Path.Combine(DataDir, "blobs");

    /// <summary>项目级记忆目录（跟着 cwd 走，不主动创建）。</summary>
    public static string ProjectMemoryDir(string cwd) =>
        Path.Combine(cwd, AppFolderName, "memories");

    /// <summary>项目级技能目录。</summary>
    public static string ProjectSkillsDir(string cwd) =>
        Path.Combine(cwd, AppFolderName, "skills");

    /// <summary>确保基础目录存在。返回是否首次创建（供首启引导用）。</summary>
    public static bool EnsureDataDirs()
    {
        var first = !Directory.Exists(DataDir);
        foreach (var dir in new[] { DataDir, SessionsDir, MemoriesDir, SkillsDir, PersonasDir, ThemesDir, UndoDir })
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return first;
    }
}
