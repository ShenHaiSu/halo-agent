using CefSharp;
using CefSharp.WinForms;

using HaloAgent.Desktop.Gpu;
using HaloAgent.Desktop.Shell;

namespace HaloAgent.Desktop.Browser;

 /// <summary>CefSettings 唯一组装入口。WinForms 必须 MultiThreadedMessageLoop=true（CEF 消息循环跑独立线程）。</summary>
public static class CefSettingsBuilder
{
    public static CefSettings Build(HostOptions opts, GpuDecision gpu)
    {
        var settings = new CefSettings
        {
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            CachePath = opts.IsDev
                ? Path.Combine(Path.GetTempPath(), "halo-cef-dev-" + Environment.ProcessId)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HaloAgent", "CefCache"),
            LogSeverity = opts.IsDev ? LogSeverity.Info : LogSeverity.Warning,
            LogFile = Path.Combine(opts.LogDir, "cef.log"),
            // 显式绝对路径：否则 CEF 按“当前目录”找，被别的程序带不同 CWD 启动时找不到子进程。
            BrowserSubprocessPath = Path.Combine(
                AppContext.BaseDirectory, "CefSharp.BrowserSubprocess.exe"),
            // SingleProcess 在 109 已移除（多进程模型固定）。
            CommandLineArgsDisabled = false,
            // WinForms 必须 true：false 时宿主得手动泵 Cef.DoMessageLoopWork，不泵则页面永不绘制 → 全黑。
            // CefSharp.WinForms 官方示例与 109 文档均为 true；此处之前写反了，这就是黑屏根因。
            MultiThreadedMessageLoop = true,
            ExternalMessagePump = false,
         };
        GpuPolicy.Apply(gpu, settings);

        // Win7+无边框误判与后台干扰。
        AddArg(settings, "disable-features",
            "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker,TranslateUI");
        AddArg(settings, "disable-breakpad", null);
        AddArg(settings, "no-first-run", null);
        AddArg(settings, "no-default-browser-check", null);
        AddArg(settings, "disable-features", "AutofillServerCommunication");
        AddArg(settings, "disable-background-networking", null);
        AddArg(settings, "disable-component-update", null);
        AddArg(settings, "disable-domain-reliability", null);
        AddArg(settings, "disable-sync", null);
        AddArg(settings, "allow-file-access-from-files", null);

        if (opts.IsDev)
        {
            AddArg(settings, "remote-debugging-port", "9222");
            AddArg(settings, "disable-application-cache", null);
            AddArg(settings, "disk-cache-size", "1");
            AddArg(settings, "media-cache-size", "1");
        }

        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = "app",
            DomainName = "local",
            SchemeHandlerFactory = new SpaSchemeHandlerFactory(
                Path.Combine(AppContext.BaseDirectory, "wwwroot")),
            IsStandard = true,
            IsLocal = true,
            IsSecure = true,
            IsCorsEnabled = true,
        });

        AppLog.Info($"CefSettings 已组装：cache={(opts.IsDev ? "临时" : "持久")} log={settings.LogSeverity} gpu={gpu}");
        return settings;
    }

    /// <summary>同名 disable-features 用逗号合并（Dictionary 不支持多值）。</summary>
    private static void AddArg(CefSettings s, string key, string? value)
    {
        if (s.CefCommandLineArgs.TryGetValue(key, out var existing)
            && key is "disable-features" or "enable-features"
            && !string.IsNullOrEmpty(existing)
            && !string.IsNullOrEmpty(value))
        {
            s.CefCommandLineArgs[key] = existing + "," + value;
            return;
        }
        s.CefCommandLineArgs[key] = value;
    }
}
