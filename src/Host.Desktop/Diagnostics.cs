using CefSharp;

namespace HaloAgent.Desktop;

/// <summary>环境诊断摘要，供 --diagnostics、日志开头、崩溃报告共用。</summary>
internal static class Diagnostics
{
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== HaloAgent 桌面宿主环境 ===");
        sb.AppendLine($"应用版本      : {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"进程 ID       : {Environment.ProcessId}");
        sb.AppendLine($"操作系统      : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"OS 内核       : {Environment.OSVersion.Version}");
        sb.AppendLine($"是 Win7       : {Program.IsWin7()}");
        sb.AppendLine($"64 位系统     : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64 位进程     : {Environment.Is64BitProcess}");
        sb.AppendLine($"运行时        : {Environment.Version}");
        sb.AppendLine($"CEF 版本      : {Cef.CefVersion}");
        sb.AppendLine($"Chromium 版本 : {Cef.ChromiumVersion}");
        sb.AppendLine($"基础目录      : {AppContext.BaseDirectory}");
        sb.AppendLine($"数据目录      : {EngineStartupDefaults.DataDir}");
        sb.AppendLine($"日志文件      : {AppLog.CurrentFile}");
        sb.AppendLine($"DPI 缩放      : {TryDpi()}");
        sb.AppendLine($"屏幕数        : {System.Windows.Forms.Screen.AllScreens.Length}");
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            sb.AppendLine($"  - {s.DeviceName} {s.Bounds} 主屏={s.Primary}");
        sb.AppendLine($"wwwroot 存在  : {Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))}");
        return sb.ToString();
    }

    private static string TryDpi()
    {
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return $"{g.DpiX}x{g.DpiY} ({(g.DpiX / 96.0):0.##}x)";
        }
        catch { return "未知"; }
    }
}
