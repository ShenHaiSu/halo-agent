using System.Runtime.InteropServices;

namespace HaloAgent.Desktop;

/// <summary>集中 P/Invoke（窗口拖拽 / 前台激活 / OLE / DLL 可加载探测）。</summary>
internal static class NativeMethods
{
    // ---------- 窗口拖拽（自绘标题栏 B 方案：前端 mousedown → haloHost.windowDragStart） ----------
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();

    /// <summary>探测 KB2533623（AddDllDirectory 是否存在）。未打补丁的 Win7 会抛 EntryPointNotFoundException。</summary>
    public static bool HasAddDllDirectory()
    {
        try
        {
            AddDllDirectory(null!);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // API 存在，只是参数不对。
            return true;
        }
    }

    /// <summary>用真实 LoadLibrary 探测某原生 DLL 是否可加载（文件存在≠能加载）。</summary>
    public static bool Present(string name)
    {
        try
        {
            var h = LoadLibraryW(name);
            if (h == IntPtr.Zero) return false;
            FreeLibrary(h);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
