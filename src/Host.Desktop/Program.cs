using System.Net;
using CefSharp;
using CefSharp.SchemeHandler;
using CefSharp.WinForms;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop;

internal static class Program
{
    /// <summary>
    /// 渐进式安全协商策略：Win7 SP1 必开 TLS 1.2，在支持 TLS 1.3 的 Win10/11 上追加开启 TLS 1.3，
    /// Win7 不支持 TLS 1.3 枚举时静默保留 TLS 1.2。
    /// </summary>
    public static void ConfigureWin7CompatibleTls()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls13;
        }
        catch (NotSupportedException)
        {
            // Win7 正常回退，仅保持 TLS 1.2
        }
    }

    [STAThread]
    static void Main()
    {
        // 0. 强制开启 TLS 1.2 (兼容 Win7 SP1)
        ConfigureWin7CompatibleTls();

        // 1. 高 DPI 自适应（兼顾 Win10/11 高分屏与 Win7 传统缩放）
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 2. 初始化 C 盘可配置存储引导与滚动日志引擎
        var storageManager = StoragePathManager.Initialize();
        HaloLogger.Initialize(storageManager);
        HaloLogger.App.Info(
            $"HaloAgent starting. OS={Environment.OSVersion}, TLS={ServicePointManager.SecurityProtocol}, DataRoot={storageManager.CurrentDataRoot}, Bootstrap={storageManager.BootstrapFilePath}");

        // 3. 全局未捕获异常兜底，写入 C 盘日志防止静默闪退
        Application.ThreadException += (_, e) => HaloLogger.App.Error("UI Thread Exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HaloLogger.App.Error("AppDomain Unhandled Exception", e.ExceptionObject as Exception);

        // 4. 配置 CefSettings（CachePath 指向用户配置的 DataRoot\cef-cache）
        var settings = new CefSettings
        {
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            CachePath = storageManager.CefCacheDirectory,
            LogFile = Path.Combine(storageManager.LogsDirectory, "cef-native.log"),
            LogSeverity = LogSeverity.Warning
        };

        // 5. Win7 显卡驱动防崩溃铁律：检测 Win7 (NT 6.1) 或 disable-gpu.flag 强制关闭 GPU 硬件加速
        bool isWin7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
        bool hasDisableGpuFlag = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "disable-gpu.flag"));
        bool gpuDisabled = isWin7 || hasDisableGpuFlag;
        if (gpuDisabled)
        {
            settings.DisableGpuAcceleration();
            settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            HaloLogger.App.Info($"GPU Acceleration disabled (isWin7={isWin7}, flag={hasDisableGpuFlag}, args=disable-gpu,disable-gpu-compositing,disable-gpu-vsync)");
        }

        // 6. 禁用 Chromium 109 在老系统上的多余后台网络探测与安全弹窗干扰
        settings.CefCommandLineArgs.Add("disable-features", "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker");

        // 7. 注册自定义本地安全协议 app://local（替代 file://，彻底解决 CORS 与 ES Module 阻断）
        var wwwrootPath = ResolveWwwrootDirectory();
        Directory.CreateDirectory(wwwrootPath);
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = "app",
            DomainName = "local",
            SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                rootFolder: wwwrootPath,
                hostName: "local",
                defaultPage: "index.html"
            ),
            IsStandard = true,
            IsLocal = true,
            IsSecure = true,
            IsCorsEnabled = true
        });

        CefSharpSettings.ConcurrentTaskExecution = true;
        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

        try
        {
            Application.Run(new MainForm(storageManager, gpuDisabled));
        }
        finally
        {
            // 8. 退出时优雅释放日志队列与 CEF 句柄，防止残留 CefSharp.BrowserSubprocess.exe 僵尸进程
            HaloLogger.App.Info("HaloAgent shutting down gracefully.");
            HaloLogger.Shutdown();
            Cef.Shutdown();
        }
    }

    private static string ResolveWwwrootDirectory()
    {
        var baseWwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(baseWwwroot, "index.html")))
        {
            return baseWwwroot;
        }

        // 若从源码目录直接运行且未复制 wwwroot，支持回溯查找项目目录下的 wwwroot
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return baseWwwroot;
    }
}
