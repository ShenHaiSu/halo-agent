using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>窗口位置与尺寸的持久化载体（逻辑像素）。</summary>
public sealed class WindowPlacement
{
    public int Left { get; set; } = 120;
    public int Top { get; set; } = 80;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public string? MonitorDeviceName { get; set; }

    public Rectangle ToRectangle() => new(Left, Top, Width, Height);

    public void FromForm(Form f)
    {
        // 最大化时 RestoreBounds 才是“还原后的位置”。
        var b = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        Maximized = f.WindowState == FormWindowState.Maximized;
        try { MonitorDeviceName = Screen.FromHandle(f.Handle).DeviceName; } catch { /* 屏幕枚举失败时忽略 */ }
    }
}
