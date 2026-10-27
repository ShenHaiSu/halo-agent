using System.Runtime.InteropServices;

namespace HaloAgent.Desktop;

/// <summary>集中 P/Invoke（KB2533623 探测 / 前台激活 / DLL 可加载探测 / OLE）。</summary>
internal static class NativeMethods
{
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
