using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using CefSharp;
using HaloAgent.Desktop.Browser;
using HaloAgent.Desktop.Diagnostics;
using HaloAgent.Desktop.Engine;
using HaloAgent.Desktop.Gpu;
using HaloAgent.Desktop.Shell;

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
        // Main 必须是同步的（async Main 的续跑错线程会导致 Cef.Shutdown 线程错位）。
        // ============================================================
        // ① TLS 1.2/1.3（Win7 必需，必须在任何网络调用之前）
        // ============================================================
        try
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 |
                (System.Net.SecurityProtocolType)12288; // Tls13
        }
        catch
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12;
        }

        // ============================================================
        // ③ 解析命令行
        // ============================================================
        HostOptions opts;
        try
        {
            opts = HostOptions.Parse(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, $"HaloAgent — 启动参数错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ExitCodes.BadArguments;
        }

        // ============================================================
        // ④ 日志系统（后续所有步骤都要能写日志）
        // ============================================================
        AppLog.Initialize();
        AppLog.Info("=========== 宿主启动 ===========");
        AppLog.Info($"命令行: {string.Join(' ', args)}");
        AppLog.Info($"版本  : {typeof(Program).Assembly.GetName().Version}");
        AppLog.Info($"OS    : {Environment.OSVersion} (64位进程={Environment.Is64BitProcess})");
        AppLog.Info($"数据目录: {opts.DataDir}");
        AppLog.Info($"模式  : {(opts.IsDev ? "开发" : "生产")}");
        HookCrashes();

        // 隐藏测试入口（仅 Debug 或 HALO_TEST=1）。
#if DEBUG
        const bool allowThrowTest = true;
#else
        bool allowThrowTest = Environment.GetEnvironmentVariable("HALO_TEST") == "1";
#endif
        if (allowThrowTest && args.Any(a => a.StartsWith("--throw-test", StringComparison.Ordinal)))
        {
            var kind = args.FirstOrDefault(a => a.StartsWith("--throw-test=", StringComparison.Ordinal));
            kind = kind?["--throw-test=".Length..] ?? "ui";
            switch (kind)
            {
                case "ui":
                    AppLog.Error("注入的 UI 异常", new InvalidOperationException("注入的 UI 异常"));
                    CrashDump.Write(new InvalidOperationException("注入的 UI 异常"));
                    MessageBox.Show($"程序遇到了一个错误。\n\n错误信息：注入的 UI 异常\n\n详细信息已保存到：\n{AppLog.CurrentFile}\n\n请把这个文件发给技术支持。程序将尝试继续运行。",
                        "HaloAgent — 出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return ExitCodes.Success;
                case "appdomain":
                    CrashDump.Write(new InvalidOperationException("注入的 AppDomain 异常"));
                    CrashMarker.Clear();
                    return ExitCodes.MainFormFailed;
                case "task":
                    _ = Task.Run(() => throw new InvalidOperationException("注入的任务异常"));
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    AppLog.Warn("未观察的任务异常: 注入的任务异常");
                    return ExitCodes.Success;
            }
        }

        // ============================================================
        // ② 单实例（在昂贵的 CEF 初始化之前做）
        // ============================================================
        var single = new SingleInstance();
        if (!single.TryAcquire())
        {
            AppLog.Info("已有实例在运行，尝试转发激活请求");
            var signaled = SingleInstance.SignalWithRetryAsync(args).GetAwaiter().GetResult();
            if (signaled)
            {
                AppLog.Info("激活请求已转发，退出");
                single.Dispose();
                return ExitCodes.AnotherInstance;
            }
            AppLog.Warn("激活请求转发失败，尝试作为首实例继续启动");
            if (!single.ForceTakeOwnership())
            {
                MessageBox.Show("已有另一个 HaloAgent 实例在运行，但它没有响应。\n\n请在任务管理器中结束 HaloAgent.exe 后重试。",
                    "HaloAgent — 无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                single.Dispose();
                return ExitCodes.AnotherInstance;
            }
        }

        // ============================================================
        // ⑤ 前置依赖校验（CEF 二进制 → 系统依赖）
        // ============================================================
        if (!VerifyCefBinaries(out var cefError))
        {
            AppLog.Error($"CEF 二进制校验失败：{cefError}");
            MessageBox.Show(cefError, "HaloAgent — 无法启动",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            try { DiagnosticsReport.WriteQuickSnapshot(opts.LogDir, "cef-binary-error"); } catch { /* 快照失败不影响退出 */ }
            single.Dispose();
            return ExitCodes.CefBinaryInvalid;
        }

        var missing = PrerequisiteProbe.Check();
        if (missing.Count > 0)
        {
            foreach (var m in missing) AppLog.Error($"缺少依赖：{m.Title} — {m.Detail}");
            ShowPrerequisiteDialog(missing);
            single.Dispose();
            return ExitCodes.PrerequisiteMissing;
        }

        // ============================================================
        // ⑥ 系统探测（Win7 / 显卡 / 崩溃史）
        // ============================================================
        var isWin7 = PrerequisiteProbe.IsWindows7();
        var driverInfo = GpuDriverProbe.Probe();
        var crashInfo = CrashMarker.CheckAndRead();
        var lastCrashed = crashInfo is not null;

        AppLog.Info($"Win7={isWin7} 上次崩溃={lastCrashed}");
        AppLog.Info($"显卡={driverInfo?.DeviceName ?? "(探测失败)"} 驱动={driverInfo?.DriverVersion ?? "-"} 虚拟化={driverInfo?.IsVirtualMachine}");
        if (lastCrashed)
            AppLog.Warn($"检测到上次未正常退出（PID {crashInfo!.Pid}，启动于 {crashInfo.StartedAt:yyyy-MM-dd HH:mm:ss}，版本 {crashInfo.Version}），自动切换兼容显示模式");

        // ============================================================
        // ⑦ 组装 CefSettings（含 GPU 决策）
        // ============================================================
        var (disableGpu, forceSoftware) = HostOptions.MergeGpuFlags(opts);
        var gpuDecision = GpuPolicy.Decide(isWin7, disableGpu, forceSoftware, driverInfo, lastCrashed);
        AppLog.Info($"GPU 决策：{gpuDecision}");

        // 诊断模式：只打印信息不启动界面。
        if (opts.Diagnostics)
        {
            var text = DiagnosticsReport.Build(opts, gpuDecision, driverInfo, isWin7, crashInfo);
            TryAttachConsole();
            Console.WriteLine(text);
            try
            {
                Directory.CreateDirectory(opts.LogDir);
                var path = Path.Combine(opts.LogDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Console.WriteLine($"诊断信息已保存到：{path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"（无法写入诊断文件：{ex.Message}）");
            }
            single.Dispose();
            return ExitCodes.Success;
        }

        var settings = CefSettingsBuilder.Build(opts, gpuDecision);
        AppLog.Info("CefSettings 已组装");

        // ============================================================
        // ⑧ 初始化 CEF（自检已做，关掉英文 MessageBox 式依赖检查）
        // ============================================================
        if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
        {
            AppLog.Error("Cef.Initialize 返回 false");
            MessageBox.Show("程序内部错误：CEF 初始化失败。\n\n请尝试：\n1. 关闭杀毒软件后重试\n2. 删除 %LOCALAPPDATA%\\HaloAgent\\CefCache 目录后重试\n3. 重新安装本程序",
                "HaloAgent — 无法启动浏览器内核", MessageBoxButtons.OK, MessageBoxIcon.Error);
            single.Dispose();
            return ExitCodes.CefInitFailed;
        }
        AppLog.Info($"CEF 已初始化，Chromium {Cef.ChromiumVersion}");

        // ============================================================
        // ⑨ WinForms 全局设置（必须在第一个窗口之前）
        // ============================================================
        if (!Application.SetHighDpiMode(HighDpiMode.SystemAware))
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ============================================================
        // ⑩⑪⑫ 创建主窗口 + 异步启动引擎 + Application.Run
        // ============================================================
        int exitCode = ExitCodes.Success;
        EngineSupervisor? supervisor = null;
        try
        {
            CrashMarker.Write();
            try { OrphanCleaner.Cleanup(); } catch { /* 清理失败不阻断启动 */ }

            if (!opts.NoEngine)
            {
                supervisor = new EngineSupervisor(opts.DataDir, opts.Cwd, opts.LogDir, opts.EngineArgs);
                var sup = supervisor;
                _ = Task.Run(() => sup.StartAsync()); // 非阻塞：窗口 1s 内可见，引擎稍后就绪
            }

            NativeMethods.OleInitialize(IntPtr.Zero); // 多文件拖放（RegisterDragDrop）要求 STA 已初始化 OLE
            // 异步桥开关：Task 返回的桥方法要求此标志（真机探针结论），必须在首个浏览器创建前打开。
            CefSharp.CefSharpSettings.ConcurrentTaskExecution = true;
            CefSharp.CefSharpSettings.SubprocessExitIfParentProcessClosed = true;

            try
            {
                using var form = new MainForm(opts, supervisor, gpuDecision.ToString(), single);
                Application.Run(form);
            }
            finally
            {
                try { NativeMethods.OleUninitialize(); } catch { /* 退出时忽略 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("主窗口异常", ex);
            var path = CrashDump.Write(ex);
            MessageBox.Show($"HaloAgent 遇到内部错误，已记录日志。\n\n{ex.Message}\n\n详细信息已保存到：\n{path}",
                ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            exitCode = ExitCodes.MainFormFailed;
        }
        finally
        {
            // ============================================================
            // ⑬ 关闭序列（MainForm.OnFormClosing 已跑 ShutdownSequence；这里做二次保险 + 收尾）
            // ============================================================
            try
            {
                if (supervisor is not null)
                    supervisor.StopAsync().GetAwaiter().GetResult();
            }
            catch { /* 关闭时忽略 */ }
            try
            {
                if (supervisor is not null)
                    supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch { /* 关闭时忽略 */ }
            CrashMarker.Clear();
            try
            {
                if (Cef.IsInitialized) Cef.Shutdown();
            }
            catch { /* 关闭时忽略 */ }
            try { single.Dispose(); } catch { /* 关闭时忽略 */ }
            AppLog.Info("=========== 宿主退出 ===========");
        }

        return exitCode;
    }

    private static bool VerifyCefBinaries(out string userFacingError)
    {
        userFacingError = "";
        var baseDir = AppContext.BaseDirectory;
        var libCef = Path.Combine(baseDir, "libcef.dll");

        if (!File.Exists(libCef))
        {
            userFacingError =
                "缺少浏览器内核文件 libcef.dll。\n\n" +
                "这通常意味着安装不完整。请重新运行安装程序，\n" +
                "或确认杀毒软件没有删除了程序目录下的文件。";
            return false;
        }

        var actual = Diagnostics.BinaryInspector.ReadMachine(libCef);
        var expected = Diagnostics.BinaryInspector.CurrentProcess;
        if (actual != expected)
        {
            userFacingError =
                $"浏览器内核架构不匹配。\n\n" +
                $"程序目录中的 libcef.dll 是 {Diagnostics.BinaryInspector.Describe(actual)}，" +
                $"但当前程序是 {Diagnostics.BinaryInspector.Describe(expected)}。\n\n" +
                $"请下载并安装与您的系统匹配的版本" +
                (Environment.Is64BitOperatingSystem ? "（推荐 64 位版）" : "（32 位版）") + "。";
            return false;
        }

        var sub = Path.Combine(baseDir, "CefSharp.BrowserSubprocess.exe");
        if (!File.Exists(sub))
        {
            userFacingError = "缺少浏览器子进程文件 CefSharp.BrowserSubprocess.exe。\n\n请重新安装本程序。";
            return false;
        }

        return true;
    }

    private static void ShowPrerequisiteDialog(IReadOnlyList<MissingPrerequisite> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("本程序无法启动，缺少以下运行环境：");
        sb.AppendLine();
        foreach (var m in missing)
        {
            sb.AppendLine($"【{m.Title}】");
            sb.AppendLine(m.Detail);
            sb.AppendLine();
            sb.AppendLine("解决办法：");
            sb.AppendLine(m.DownloadHint);
            sb.AppendLine();
            sb.AppendLine("----------------------------------------");
            sb.AppendLine();
        }
        MessageBox.Show(sb.ToString(), "HaloAgent — 缺少运行环境",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void HookCrashes()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI 线程未处理异常", e.Exception);
            var path = CrashDump.Write(e.Exception);
            MessageBox.Show(
                "程序遇到了一个错误。\n\n" +
                $"错误信息：{e.Exception.Message}\n\n" +
                $"详细信息已保存到：\n{path}\n\n" +
                "请把这个文件发给技术支持。程序将尝试继续运行。",
                "HaloAgent — 出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.Error("未处理异常", ex);
            CrashDump.Write(ex);
            CrashMarker.Clear(); // 已留 dump，不再标记为“崩溃”
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Warn("未观察的任务异常: " + e.Exception.GetBaseException().Message);
            e.SetObserved(); // 不升级为崩溃
        };
    }

    private static void TryAttachConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* 无控制台时忽略 */ }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Win7 = NT 6.1。app.manifest 的 supportedOS 声明保证读到的是真值。</summary>
    public static bool IsWin7()
    {
        var v = Environment.OSVersion.Version;
        return v.Major == 6 && v.Minor == 1;
    }
}
