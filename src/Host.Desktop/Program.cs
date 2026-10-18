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
         TraceStartup("stage=main-enter");
         // 0. 强制开启 TLS 1.2 (兼容 Win7 SP1)
         ConfigureWin7CompatibleTls();
 
         // 1. 高 DPI 自适应（兼顾 Win10/11 高分屏与 Win7 传统缩放）
         Application.SetHighDpiMode(HighDpiMode.SystemAware);
         Application.EnableVisualStyles();
         Application.SetCompatibleTextRenderingDefault(false);
 
         // 2. 全局未捕获异常兜底【必须先于存储/日志初始化注册】，避免启动期异常导致无日志闪退
         Application.ThreadException += (_, e) =>
         {
             WriteStartupErrorFile("UI Thread Exception", e.Exception);
             TryLogFatal("UI Thread Exception", e.Exception);
         };
         AppDomain.CurrentDomain.UnhandledException += (_, e) =>
         {
             WriteStartupErrorFile("AppDomain Unhandled Exception", e.ExceptionObject as Exception);
             TryLogFatal("AppDomain Unhandled Exception", e.ExceptionObject as Exception);
         };
         TraceStartup("stage=handlers-registered");
 
         // 3. 初始化 C 盘可配置存储引导与滚动日志引擎（失败时弹窗明示而非静默闪退）
         StoragePathManager storageManager;
         TraceStartup("stage=before-storage-init");
         try
         {
             storageManager = StoragePathManager.Initialize();
             TraceStartup("stage=storage-init-ok root=" + storageManager.CurrentDataRoot);
             HaloLogger.Initialize(storageManager);
             TraceStartup("stage=logger-init-ok");
         }
         catch (Exception ex)
         {
             TraceStartup("stage=storage-init-FAILED: " + ex.GetType().Name + ": " + ex.Message);
             TraceStartup("stage=init-report: " + StoragePathManager.LastInitReport);
             ShowFatalStartupError("存储初始化失败", ex);
             return;
         }
         HaloLogger.App.Info(
             $"HaloAgent starting. OS={Environment.OSVersion}, TLS={ServicePointManager.SecurityProtocol}, DataRoot={storageManager.CurrentDataRoot}, Bootstrap={storageManager.BootstrapFilePath}");
        // need17-07：Python 可用性后台快照（不阻塞 UI/网关启动；失败只记日志）
        _ = Task.Run(() =>
        {
            try { Host.Desktop.Tools.Process.PythonAvailabilitySnapshot.Refresh(); }
            catch (Exception ex) { Host.Desktop.Infrastructure.Logging.HaloLogger.App.Error("Python availability snapshot refresh failed", ex); }
        });
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
         TraceStartup("stage=before-cef-init");
         try
         {
             Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
             TraceStartup("stage=cef-init-ok");
         }
         catch (Exception ex)
         {
             TraceStartup("stage=cef-init-FAILED: " + ex.GetType().Name + ": " + ex.Message);
             ShowFatalStartupError("浏览器内核初始化失败（常见于缺失 VC++ 运行库或 KB2533623 补丁）", ex);
             return;
         }
 
        try
        {
            TraceStartup("stage=before-app-run");
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

     private static string StartupTracePath()
     {
         try
         {
             return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-trace.txt");
         }
         catch
         {
             return Path.Combine(Path.GetTempPath(), "HaloAgent-startup-trace.txt");
         }
     }
 
     private static void TraceStartup(string line)
     {
         try
         {
             File.AppendAllText(
                 StartupTracePath(),
                 DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
         }
         catch
         {
         }
     }
 
     private static void WriteStartupErrorFile(string context, Exception? ex)
     {
         try
         {
             var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt");
             var body =
                 DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + context + Environment.NewLine
                 + (ex == null ? "(no exception)" : ex.ToString()) + Environment.NewLine;
             File.WriteAllText(path, body);
         }
         catch
         {
         }
     }
 
     private static void TryLogFatal(string context, Exception? ex)
     {
         try
         {
             if (ex != null)
             {
                 HaloLogger.App.Error(context, ex);
             }
             else
             {
                 HaloLogger.App.Error(context);
             }
         }
         catch
         {
         }
     }
 
     private static void ShowFatalStartupError(string title, Exception ex)
     {
         WriteStartupErrorFile(title, ex);
         TryLogFatal(title, ex);
         try
         {
             HaloLogger.Shutdown();
         }
         catch
         {
         }
         var message =
             title + "，程序无法启动。\n\n原因: " + ex.Message + "\n\n"
             + "排查建议:\n"
             + "1. 以管理员身份运行一次（自动修复 C盘数据目录权限）；\n"
             + "2. 检查杀毒软件是否拦截了本程序的文件写入；\n"
             + "3. 缺失 VC++ 2015-2022 运行库或 Win7 KB2533623 补丁也会导致内核初始化失败；\n"
             + "4. 可设置环境变量 HALO_BOOTSTRAP_PATH 指向可写位置后重试。\n\n"
             + "详细日志请查看: C盘 HaloAgentData 目录下 logs 文件夹。";
         System.Windows.Forms.MessageBox.Show(
             message,
             "Halo-Agent 启动失败 - " + title,
             System.Windows.Forms.MessageBoxButtons.OK,
             System.Windows.Forms.MessageBoxIcon.Error);
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
