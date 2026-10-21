using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Process;

namespace Host.Desktop.Startup;

/// <summary>need21-B01: 单实例守卫（原 Program.cs 私有方法整体搬迁，行为零变化）。</summary>
public static class ProgramSingleInstance
{
    public static bool TryAcquireAppGuard(Action<string> trace, Action<string> warn)
    {
        SingleInstanceGuard.TraceSink = s => trace(s);
        NativeWindowActivator.TraceSink = s => trace(s);
        if (SingleInstanceGuard.ShouldBypass())
        {
            trace("stage=single-instance-bypass");
            return true;
        }
        string probe;
        try
        {
            probe = SingleInstanceGuard.ResolveBootstrapProbePath();
        }
        catch (Exception ex)
        {
            trace("stage=single-instance-probe-FAILED: " + ex.Message);
            return true;
        }
        SingleInstanceGuard? guard;
        string mutexName;
        try
        {
            guard = SingleInstanceGuard.TryAcquireApp(probe, out mutexName);
        }
        catch (Exception ex)
        {
            trace("stage=app-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return true;
        }
        if (guard == null)
        {
            trace($"stage=app-mutex-null name={mutexName}");
            return true;
        }
        if (!guard.IsFirstInstance)
        {
            trace($"stage=single-instance-blocked scope=app name={mutexName}");
            warn($"Second instance blocked (app mutex {mutexName}).");
            NativeWindowActivator.TryBringToFront("Halo-Agent Desktop");
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    "Halo-Agent 已在运行中，已为你唤起已有窗口。",
                    "Halo-Agent",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information
                );
            }
            catch { }
            guard.Dispose();
            return false;
        }
        Program.GuardApp = guard;
        trace($"stage=single-instance-ok scope=app name={mutexName}");
        return true;
    }

    public static bool TryAcquireDataGuard(
        string dataRoot,
        Action<string> trace,
        Action<string> warn
    )
    {
        if (SingleInstanceGuard.ShouldBypass())
            return true;
        SingleInstanceGuard? guard;
        string mutexName;
        string lockPath;
        try
        {
            guard = SingleInstanceGuard.TryAcquireData(dataRoot, out mutexName, out lockPath);
        }
        catch (Exception ex)
        {
            trace("stage=data-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            ProgramBoot.ShowFatalStartupError("数据目录锁初始化失败", ex);
            Program.GuardApp?.Dispose();
            Program.GuardApp = null;
            return false;
        }
        if (guard == null || !guard.IsFirstInstance)
        {
            trace($"stage=single-instance-blocked scope=data name={mutexName}");
            warn($"Second instance blocked (data mutex {mutexName}).");
            NativeWindowActivator.TryBringToFront("Halo-Agent Desktop");
            ProgramBoot.ShowFatalStartupError(
                "数据目录已被另一实例占用",
                new IOException($"DataRoot '{dataRoot}' 已被占用。")
            );
            guard?.Dispose();
            Program.GuardApp?.Dispose();
            Program.GuardApp = null;
            return false;
        }
        Program.GuardData = guard;
        trace($"stage=single-instance-ok scope=data name={mutexName}");
        return true;
    }
}
