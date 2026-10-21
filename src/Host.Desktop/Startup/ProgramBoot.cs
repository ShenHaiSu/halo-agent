using System.Net;
using CefSharp;
using CefSharp.SchemeHandler;
using CefSharp.WinForms;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Process;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Startup;

/// <summary>need21-B01: Program 瘦身目标文件。原 Program.cs 300+ 行初始化逻辑逐段搬入此处。</summary>
public static class ProgramBoot
{
    public static void TraceStartup(string line)
    {
        try
        {
            File.AppendAllText(
                StartupTracePath(),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine
            );
        }
        catch { }
    }

    public static string StartupTracePath()
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

    public static void WriteStartupErrorFile(string context, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt");
            File.WriteAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " "
                    + context
                    + Environment.NewLine
                    + (ex == null ? "(no exception)" : ex.ToString())
                    + Environment.NewLine
            );
        }
        catch { }
    }

    public static void TryLogFatal(string context, Exception? ex)
    {
        try
        {
            if (ex != null)
                HaloLogger.App.Error(context, ex);
            else
                HaloLogger.App.Error(context);
        }
        catch { }
    }

    public static void TryLogWarn(string message)
    {
        try
        {
            HaloLogger.App.Warn(message);
        }
        catch { }
    }

    public static void ShowFatalStartupError(string title, Exception ex)
    {
        WriteStartupErrorFile(title, ex);
        TryLogFatal(title, ex);
        try
        {
            HaloLogger.Shutdown();
        }
        catch { }
        System.Windows.Forms.MessageBox.Show(
            title + "，程序无法启动。\n\n原因: " + ex.Message,
            "Halo-Agent 启动失败 - " + title,
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error
        );
    }

    public static string ResolveWwwrootDirectory()
    {
        var baseWwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(baseWwwroot, "index.html")))
            return baseWwwroot;
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
            dir = dir.Parent;
        }
        return baseWwwroot;
    }

    public static CefSettings BuildCefSettings(
        StoragePathManager storageManager,
        out bool gpuDisabled
    )
    {
        bool isWin7 =
            Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
        bool hasFlag = File.Exists(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "disable-gpu.flag")
        );
        gpuDisabled = isWin7 || hasFlag;
        var settings = new CefSettings
        {
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            CachePath = storageManager.CefCacheDirectory,
            LogFile = Path.Combine(storageManager.LogsDirectory, "cef-native.log"),
            LogSeverity = LogSeverity.Warning
        };
        if (gpuDisabled)
        {
            settings.DisableGpuAcceleration();
            settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
        }
        settings.CefCommandLineArgs.Add(
            "disable-features",
            "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker"
        );
        var wwwrootPath = ResolveWwwrootDirectory();
        Directory.CreateDirectory(wwwrootPath);
        settings.RegisterScheme(
            new CefCustomScheme
            {
                SchemeName = "app",
                DomainName = "local",
                SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                    wwwrootPath,
                    "local",
                    "index.html"
                ),
                IsStandard = true,
                IsLocal = true,
                IsSecure = true,
                IsCorsEnabled = true
            }
        );
        return settings;
    }

    public static void RefreshPythonSnapshotFireAndForget()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Host.Desktop.Tools.Process.PythonAvailabilitySnapshot.Refresh();
            }
            catch (Exception ex)
            {
                try
                {
                    HaloLogger.App.Error("Python availability snapshot refresh failed", ex);
                }
                catch { }
            }
        });
    }
}
