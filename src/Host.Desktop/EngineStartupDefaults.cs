namespace HaloAgent.Desktop;

/// <summary>宿主与引擎共用的路径约定。**只此一份**，两边从同一个常量推路径。</summary>
public static class EngineStartupDefaults
{
    public static string DataDir
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(over)) return over.Trim();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Path.GetTempPath(), "haloagent");
            return Path.Combine(home, ".haloagent");
        }
    }

    public static string LogsDir => Path.Combine(DataDir, "logs");
}
