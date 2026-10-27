using System.Windows.Forms;
using CefSharp;
using CefSharp.SchemeHandler;
using CefSharp.WinForms;
using HaloAgent.Desktop.Bridge;
using HaloAgent.Desktop.Engine;
using HaloAgent.Desktop.Handlers;

namespace HaloAgent.Desktop;

internal static class Program
{
    /// <summary>产品名（窗口标题 / 日志 / 数据目录共用一份）</summary>
    public const string ProductName = "HaloAgent";

    /// <summary>应用内自定义协议：生产态加载 app://local/index.html</summary>
    public const string AppScheme = "app";
    public const string AppHost = "local";

    [STAThread]
    private static int Main(string[] args)
    {
        // 注意：Main 必须是同步的（不能是 async Task Main）。
        // 教训：async Main 里第一次 await 之后续跑在线程池线程，
        // finally 中的 Cef.Shutdown() 就会跑错线程，
        // CefSharp 要求 Shutdown 与 Initialize 同线程（UI 线程），否则抛异常。
        // 这里的 GetAwaiter().GetResult() 不会死锁：
        // 调用点都在消息循环启动之前/结束之后，没有 SynchronizationContext 截留。
        // ============================================================
        // 第 1 步（必须最先）：TLS 1.2 / 1.3
        // Win7 默认不启用 TLS 1.2，C# 的 HttpClient 访问现代 HTTPS 端点
        // 会在握手阶段直接失败（报 "底层连接已关闭"）。
        // 放在最顶部，早于任何可能的网络调用。
        // ============================================================
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

        // ============================================================
        // 第 2 步：DPI 策略
        // Win10/11 用 PerMonitorV2（多显示器不同缩放时清晰）；
        // Win7 不支持 PerMonitorV2，SetHighDpiMode 会返回 false，
        // 此时降级到 SystemAware（Win7 唯一可用的方式）。
        // ============================================================
        bool dpiOk = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (!dpiOk)
        {
            dpiOk = Application.SetHighDpiMode(HighDpiMode.SystemAware);
        }
        if (!dpiOk)
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ============================================================
        // 第 3 步：崩溃兜底（必须在 Cef.Initialize 之前挂上）
        // ============================================================
        AppLog.Initialize();
        HookCrashes();

        bool isWin7 = IsWin7();

        // ============================================================
        // 第 4 步：CEF 设置
        // ============================================================
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductName, "cef-cache");
        Directory.CreateDirectory(dataDir);

        var settings = new CefSettings
        {
            // 中文界面
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",

            // IndexedDB / localStorage 落盘位置（前端保存主题/侧栏状态靠它）
            CachePath = dataDir,
            RootCachePath = dataDir,

            // 日志：Error 足够，Info 会产生大量噪音
            LogSeverity = LogSeverity.Error,
            LogFile = Path.Combine(dataDir, "cef.log"),

            // 退出时的清理行为
            PersistSessionCookies = true,
            PersistUserPreferences = true,
        };

        // ---- Win7 显卡驱动防黑屏铁律 ----
        // 老旧核显/驱动下，Chromium 的 GPU 合成极易黑屏、花屏、白屏。
        // 判据：Win7 一律关；另外允许用一个 flag 文件手动关（Win10 笔记本老驱动也需要）。
        bool forceNoGpu =
            isWin7 ||
            File.Exists(Path.Combine(AppContext.BaseDirectory, "disable-gpu.flag")) ||
            Environment.GetEnvironmentVariable("HALO_DISABLE_GPU") == "1";

        if (forceNoGpu)
        {
            settings.DisableGpuAcceleration();
            settings.CefCommandLineArgs.Add("disable-gpu-compositing", "1");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync", "1");
            settings.CefCommandLineArgs.Add("disable-software-rasterizer", "0");
        }

        // ---- 去掉会干扰桌面端的老系统特性 ----
        settings.CefCommandLineArgs.Add(
            "disable-features",
            // CalculateNativeWinOcclusion: Win7 上会误判窗口被遮挡 → 停止绘制 → 白屏
            // WinUseBrowserSpellChecker: 老系统拼写检查与输入法打架
            // HardwareMediaKeyHandling: 会抢占全局媒体键
            "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker,HardwareMediaKeyHandling");

        // 禁用不需要的后台服务，减少 Win7 上的内存与崩溃面
        settings.CefCommandLineArgs.Add("disable-background-networking", "1");
        settings.CefCommandLineArgs.Add("disable-component-update", "1");
        settings.CefCommandLineArgs.Add("disable-default-apps", "1");
        settings.CefCommandLineArgs.Add("no-first-run", "1");
        settings.CefCommandLineArgs.Add("no-default-browser-check", "1");

        // ---- 注册自定义协议 ----
        // 为什么不用 file:// ：
        //   ES Module、Fetch、Web Worker、字体在同源策略下会被 file:// 直接拒绝。
        // 用 app:// 就等价于 https:// 的完整安全上下文。
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = AppScheme,
            DomainName = AppHost,
            SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                rootFolder: wwwroot,
                schemeName: AppScheme,
                hostName: AppHost,
                defaultPage: "index.html"),
            IsStandard = true,
            IsLocal = true,
            IsSecure = true,
            IsCorsEnabled = true,
        });

        // ---- CEF 初始化 ----
        if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
        {
            MessageBox.Show(
                "Chromium 内核初始化失败。\n\n" +
                "常见原因：\n" +
                "  1. 缺少 VC++ 2015-2022 运行库（请安装 vc_redist）\n" +
                "  2. Windows 7 未安装 KB2533623 补丁\n" +
                "  3. libcef.dll 被杀毒软件隔离\n\n" +
                "日志：" + AppLog.CurrentFile,
                ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        int exitCode;
        var engine = new EngineClient();
        try
        {
            var startup = DesktopStartup.Parse(args);
            if (startup.Diagnostics)
            {
                Console.WriteLine(Diagnostics.Describe());
                return 0;
            }

            // 引擎启动失败不阻断窗口：页面会显示"引擎未运行"，仍可做外观验收。
            try
            {
                engine.StartAsync(EngineStartupDefaults.DataDir).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Error("引擎启动失败（宿主仍继续运行）", ex);
            }

            CefSharp.CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
            // 关键：HostBridge 的方法全部返回 Task<T>（异步桥），CefSharp 109 默认
            // 不支持 Task 返回值，必须在创建第一个 ChromiumWebBrowser 之前打开此开关，
            // 否则 JS 调用会抛 "Your method returned a Task which is not supported..."。
            // （真机探针实测结论，见 docs/evidence/P0/ACCEPTANCE-NOTES.md §4。）
            CefSharp.CefSharpSettings.ConcurrentTaskExecution = true;
            Application.Run(new MainForm(startup, engine));
            exitCode = 0;
        }
        finally
        {
            // ---- 优雅释放：不 Shutdown 会留下 BrowserSubprocess 僵尸进程 ----
            // 与 Initialize 同线程（UI 线程，见 Main 注释）。
            try { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            Cef.Shutdown();
        }

        return exitCode;
    }

    private static void HookCrashes()
    {
        // UI 线程未处理异常：不弹系统黄框，记日志并提示重启
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI 线程未处理异常", e.Exception);
            ShowFatal(e.Exception);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // 其它线程
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("AppDomain 未处理异常", e.ExceptionObject as Exception);
        };

        // 忘记 await 的 Task 异常：只记日志，不弹窗（容易误报）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Warn("未观察的 Task 异常: " + e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            $"HaloAgent 遇到内部错误，已记录日志。\n\n{ex.Message}\n\n日志：{AppLog.CurrentFile}",
            ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Win7 = NT 6.1。app.manifest 里的 supportedOS 声明保证了这里读到的值是真的。</summary>
    public static bool IsWin7()
    {
        var v = Environment.OSVersion.Version;
        return v.Major == 6 && v.Minor == 1;
    }
}
