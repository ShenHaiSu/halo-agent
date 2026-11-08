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
        var wwwrootPath = ResolveWwwrootDirectory();
        Directory.CreateDirectory(wwwrootPath);
        // 前端指纹变化即清 HTTP 磁盘缓存（只删 Cache/Code Cache/GPUCache，保留 Local Storage 主题等）。
        // 原因：app://local 经 FolderSchemeHandler 走 HTTP 缓存，index.html 被缓存后新 wwwroot 不生效；
        // 全禁 CachePath 会连 localStorage 一起丢，故只做版本门控的精准清理（Cef.Initialize 前执行，无锁）。
        EnsureFreshCefHttpCache(storageManager, wwwrootPath);
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
        settings.RegisterScheme(
            new CefCustomScheme
            {
                SchemeName = "app",
                DomainName = "local",
                SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                    wwwrootPath,
                    "app",
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

    /// <summary>前端指纹变化时清理 CEF HTTP 磁盘缓存。只删 Cache/Code Cache/GPUCache，保留 Local Storage/IndexedDB。</summary>
    private static void EnsureFreshCefHttpCache(
        StoragePathManager storageManager,
        string wwwrootPath
    )
    {
        try
        {
            var fingerprint = ComputeWwwrootFingerprint(wwwrootPath);
            var markerDir = storageManager.ConfigDirectory;
            Directory.CreateDirectory(markerDir);
            var markerPath = Path.Combine(markerDir, "cef-cache.frontend");
            string? prev = null;
            try
            {
                if (File.Exists(markerPath))
                    prev = File.ReadAllText(markerPath).Trim();
            }
            catch { }
            if (
                !string.IsNullOrEmpty(prev)
                && string.Equals(prev, fingerprint, StringComparison.Ordinal)
            )
                return;
            var cacheRoot = storageManager.CefCacheDirectory;
            // Cef.Initialize 前执行，无文件锁，可安全删除。
            foreach (
                var sub in new[]
                {
                    "Cache",
                    "Code Cache",
                    "GPUCache",
                    Path.Combine("Service Worker", "CacheStorage")
                }
            )
            {
                try
                {
                    var dir = Path.Combine(cacheRoot, sub);
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
            try
            {
                File.WriteAllText(markerPath, fingerprint);
            }
            catch { }
            try
            {
                Directory.CreateDirectory(cacheRoot);
            }
            catch { }
        }
        catch { }
    }

    private static string ComputeWwwrootFingerprint(string wwwrootPath)
    {
        try
        {
            var indexPath = Path.Combine(wwwrootPath, "index.html");
            if (!File.Exists(indexPath))
                return "noindex";
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(indexPath);
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash, 0, 8);
        }
        catch
        {
            return "unknown";
        }
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
