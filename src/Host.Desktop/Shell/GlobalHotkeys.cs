using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 全局热键。固定 ID（不动态分配）；被占用返回 false（调用方记 WARN，不崩溃）；
/// UnregisterHotKey 必须在 FormClosed 前调用，否则热键被占到重启。
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, bool> _registered = new();

    public const int IdToggleWindow = 0x0001;
    public const int IdNewSession = 0x0002;

    public GlobalHotkeys(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>注册一个热键。返回 false 表示被占用。</summary>
    public bool TryRegister(int id, Keys key, KeyModifiers mods)
    {
        var ok = RegisterHotKey(_hwnd, id, (uint)mods, (uint)key);
        _registered[id] = ok;
        return ok;
    }

    public void Dispose()
    {
        foreach (var (id, ok) in _registered)
            if (ok)
                try { UnregisterHotKey(_hwnd, id); } catch { /* 注销失败可忽略 */ }
        _registered.Clear();
    }

    [Flags]
    public enum KeyModifiers : uint
    {
        None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
