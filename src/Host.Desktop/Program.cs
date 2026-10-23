using System.Net;
using CefSharp;
using CefSharp.WinForms;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Process;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Startup;

namespace Host.Desktop;

internal static class Program
{
    internal static SingleInstanceGuard? GuardApp;
    internal static SingleInstanceGuard? GuardData;
    private const int SecondInstanceExitCode = 42;

    public static void ConfigureWin7CompatibleTls()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls13;
        }
        catch (NotSupportedException) { }
    }

    [STAThread]
    static void Main()
    {
        ProgramBoot.TraceStartup("stage=main-enter");
        if (
            !ProgramSingleInstance.TryAcquireAppGuard(
                ProgramBoot.TraceStartup,
                ProgramBoot.TryLogWarn
            )
        )
        {
            Environment.ExitCode = SecondInstanceExitCode;
            return;
        }
        ConfigureWin7CompatibleTls();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
            ProgramBoot.TryLogFatal("UI Thread Exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ProgramBoot.TryLogFatal("AppDomain Unhandled", e.ExceptionObject as Exception);
        StoragePathManager storage;
        try
        {
            storage = StoragePathManager.Initialize();
            HaloLogger.Initialize(storage);
        }
        catch (Exception ex)
        {
            ProgramBoot.ShowFatalStartupError("存储初始化失败", ex);
            return;
        }
        HaloLogger.App.Info($"HaloAgent starting. DataRoot={storage.CurrentDataRoot}");
        if (
            !ProgramSingleInstance.TryAcquireDataGuard(
                storage.CurrentDataRoot,
                ProgramBoot.TraceStartup,
                ProgramBoot.TryLogWarn
            )
        )
        {
            Environment.ExitCode = SecondInstanceExitCode;
            return;
        }
        ProgramBoot.RefreshPythonSnapshotFireAndForget();
        var settings = ProgramBoot.BuildCefSettings(storage, out bool gpuDisabled);
        CefSharpSettings.ConcurrentTaskExecution = true;
        try
        {
            Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
        }
        catch (Exception ex)
        {
            ProgramBoot.ShowFatalStartupError("浏览器内核初始化失败", ex);
            return;
        }
        var provider = ServiceRegistration.AddHaloAgent(storage, null, gpuDisabled);
        try
        {
            Application.Run(new MainForm(provider, storage, gpuDisabled));
        }
        finally
        {
            GuardData?.Dispose();
            GuardData = null;
            GuardApp?.Dispose();
            GuardApp = null;
            HaloLogger.App.Info("HaloAgent shutting down gracefully.");
            HaloLogger.Shutdown();
            Cef.Shutdown();
        }
    }
}
