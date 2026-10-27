using System.Reflection;

namespace HaloAgent.Desktop;

/// <summary>构建标识：判定“是否允许开发态功能”。发布版必须拒绝 --dev。</summary>
public static class BuildInfo
{
    public static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public static bool HasDevFlag =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "dev.flag"));

    public static bool AllowsDevMode => IsDebugBuild || HasDevFlag;

    public static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    public static string BuildTime
    {
        get
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path)) return "unknown";
            try { return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); }
            catch { return "unknown"; }
        }
    }
}
