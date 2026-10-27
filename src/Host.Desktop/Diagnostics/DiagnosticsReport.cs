using System.Drawing;
using System.Text;
using System.Windows.Forms;
using CefSharp;
using HaloAgent.Desktop.Gpu;
using HaloAgent.Desktop.Shell;

namespace HaloAgent.Desktop.Diagnostics;

/// <summary>--diagnostics 输出：用户发一份文本即可定位 90% 启动问题。</summary>
public static class DiagnosticsReport
{
    public static string Build(
        HostOptions opts,
        GpuDecision gpu,
        GpuDriverInfo? driver,
        bool isWin7,
        CrashMarkerInfo? crashInfo)
    {
        var sb = new StringBuilder(2048);
        void Line(string k, string v) => sb.AppendLine($"{k,-16}: {v}");
        void Sep(string title) { sb.AppendLine(); sb.AppendLine($"---------- {title} ----------"); }

        sb.AppendLine("=========== HaloAgent 诊断 ===========");

        Sep("程序");
        Line("版本", BuildInfo.Version);
        Line("构建时间", BuildInfo.BuildTime);
        Line("进程位数", Environment.Is64BitProcess ? "64 位" : "32 位");
        Line("运行模式", opts.IsDev ? "开发" : "生产");
        Line("允许 dev", BuildInfo.AllowsDevMode ? "是" : "否");
        Line("程序目录", AppContext.BaseDirectory);

        Sep("系统");
        Line("OS 版本", Environment.OSVersion.ToString());
        Line("系统位数", Environment.Is64BitOperatingSystem ? "64 位" : "32 位");
        Line("是否 Win7", isWin7 ? "是" : "否");
        Line("计算机名", Environment.MachineName);
        Line("用户名", Environment.UserName);
        Line("处理器数", Environment.ProcessorCount.ToString());

        Sep("浏览器内核");
        Line("Chromium", Cef.ChromiumVersion);
        Line("CefSharp", Cef.CefSharpVersion);
        var libcef = Path.Combine(AppContext.BaseDirectory, "libcef.dll");
        Line("libcef.dll", FormatFile(libcef, checkPe: true));
        Line("子进程", FormatFile(Path.Combine(AppContext.BaseDirectory, "CefSharp.BrowserSubprocess.exe")));
        Line("icudtl.dat", FormatFile(Path.Combine(AppContext.BaseDirectory, "icudtl.dat")));
        Line("resources.pak 数", Directory.Exists(AppContext.BaseDirectory)
            ? Directory.GetFiles(AppContext.BaseDirectory, "*.pak").Length.ToString() : "?");

        Sep("显示 / GPU");
        Line("GPU 决策", gpu.ToString());
        Line("显卡", driver?.DeviceName ?? "(注册表探测失败)");
        Line("驱动提供者", driver?.DriverName ?? "-");
        Line("驱动版本", driver?.DriverVersion ?? "-");
        Line("虚拟化", driver?.IsVirtualMachine == true ? "是" : "否");
        Line("flag 文件", string.Join(", ",
            new[] { "disable-gpu.flag", "force-software.flag" }
                .Where(f => File.Exists(Path.Combine(AppContext.BaseDirectory, f)))
                .DefaultIfEmpty("无")));

        if (Screen.AllScreens.Length > 0)
        {
            foreach (var s in Screen.AllScreens)
                Line(s.Primary ? "主显示器" : "显示器",
                    $"{s.DeviceName} {s.Bounds.Width}×{s.Bounds.Height} @{s.Bounds.X},{s.Bounds.Y} (工作区 {s.WorkingArea.Width}×{s.WorkingArea.Height})");
        }
        else Line("显示器", "(枚举失败)");

        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            Line("系统 DPI", $"{g.DpiX} ({(int)Math.Round(g.DpiX / 96.0 * 100)}%)");
        }
        catch { Line("系统 DPI", "(读取失败)"); }

         Sep("依赖");
         // vcruntime140_1.dll 仅由 x64 运行库提供，32 位进程不要求（见 PrerequisiteProbe）。
         var vcDlls = Environment.Is64BitProcess
             ? new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" }
             : new[] { "vcruntime140.dll", "msvcp140.dll" };
         foreach (var dll in vcDlls)
             Line(dll, NativeMethods.Present(dll) ? "可用" : "缺失");
        Line("KB2533623", !isWin7 ? "不适用"
            : NativeMethods.HasAddDllDirectory() ? "已安装" : "缺失");

        Sep("路径");
        Line("数据目录", opts.DataDir);
        Line("日志目录", opts.LogDir);
        Line("工作目录", opts.Cwd);

        Sep("上次运行");
        if (crashInfo is null) Line("状态", "正常退出（或首次运行）");
        else
        {
            Line("状态", "★ 未正常退出");
            Line("上次 PID", crashInfo.Pid.ToString());
            Line("上次启动", crashInfo.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            Line("上次版本", crashInfo.Version ?? "?");
        }

        sb.AppendLine();
        sb.AppendLine("=========== 诊断结束 ===========");
        return sb.ToString();
    }

    private static string FormatFile(string path, bool checkPe = false)
    {
        if (!File.Exists(path)) return "缺失";
        try
        {
            var len = new FileInfo(path).Length;
            var size = len >= 1024 * 1024 ? $"{len / 1024.0 / 1024.0:F1} MB" : $"{len / 1024.0:F1} KB";
            var arch = checkPe ? BinaryInspector.Describe(BinaryInspector.ReadMachine(path)) : null;
            return arch is null ? $"存在 ({size})" : $"存在 ({size}, {arch})";
        }
        catch (Exception ex) { return $"存在（读取失败：{ex.Message}）"; }
    }

    /// <summary>启动早期失败时的最小快照（CEF/Screen 可能取不到）。</summary>
    public static string WriteQuickSnapshot(string logDir, string reason)
    {
        var path = Path.Combine(logDir, $"diagnostics-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"=========== 快速诊断（{reason}）===========");
        sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"版本      : {BuildInfo.Version}");
        sb.AppendLine($"进程位数  : {(Environment.Is64BitProcess ? "64" : "32")}");
        sb.AppendLine($"OS        : {Environment.OSVersion}");
        sb.AppendLine($"程序目录  : {AppContext.BaseDirectory}");
        sb.AppendLine($"当前目录  : {Environment.CurrentDirectory}");
        sb.AppendLine();
        foreach (var f in new[]
        {
            "libcef.dll", "CefSharp.Core.Runtime.dll", "CefSharp.dll", "CefSharp.WinForms.dll",
            "CefSharp.BrowserSubprocess.exe", "icudtl.dat", "HaloAgent.exe", "HaloAgent.Engine.exe",
        })
            sb.AppendLine($"{f,-34}: {FormatFile(Path.Combine(AppContext.BaseDirectory, f))}");
        try
        {
            Directory.CreateDirectory(logDir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch { return "(无法写入)"; }
    }
}
