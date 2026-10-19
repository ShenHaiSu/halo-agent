using System.Runtime.InteropServices;

namespace Host.Desktop.Infrastructure.Process;

/// <summary>
/// need18-04：老窗口唤起。L2 广播（WM_SHOWME，标题无关）+ L1 精确（FindWindow，同步）。
/// 全部包 try/catch，失败返回 false（唤起失败不阻塞第二实例退出，只 trace）。
/// </summary>
public static class NativeWindowActivator
{
    public const int WM_SHOWME = 0x0400 + 0x1F; // WM_APP+0x1F，私有区，不与系统冲突

    /// <summary>诊断行接收器。Program 启动时接到私有 TraceStartup；单测可订阅断言关键字。</summary>
    public static Action<string>? TraceSink { get; set; }

    public static bool TryBringToFront(string windowTitle)
    {
        try
        {
            bool broadcastOk = false;
            try { broadcastOk = BroadcastShowMe(); } catch { broadcastOk = false; }
            bool byTitleOk = false;
            try { byTitleOk = BringByTitle(windowTitle); } catch { byTitleOk = false; }
            bool ok = broadcastOk || byTitleOk;
            Emit(ok ? $"activate-ok broadcast={broadcastOk} title={byTitleOk}" : "activate-failed");
            return ok;
        }
        catch
        {
            Emit("activate-failed");
            return false;
        }
    }

    public static bool BroadcastShowMe()
    {
        try
        {
            return PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    public static bool BringByTitle(string title)
    {
        try
        {
            if (string.IsNullOrEmpty(title)) return false;
            var h = FindWindow(null, title);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            }
            catch { }
            try { SetForegroundWindow(h); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Emit(string line)
    {
        try { TraceSink?.Invoke(line); } catch { }
    }

    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
