using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 无边框主窗口基类。用 NCHITTEST 返回 HTCAPTION 让系统接管拖拽
/// （双击最大化/Win+方向键/Aero Snap 免费获得），代价是处理 WM_NCCALCSIZE 留边。
/// </summary>
public class ChromeForm : Form
{
    private const int ResizeBorder = 6;
    private const int TitleBarButtonZoneWidth = 200;

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_DPICHANGED = 0x02E0;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>标题栏高度（前端自绘，此处只用于 NCHITTEST 判定）。</summary>
    public int TitleBarHeight { get; set; } = 40;

    private Rectangle[] _webDragRegions = Array.Empty<Rectangle>();

    /// <summary>DPI 变化时触发，参数是新 DPI。</summary>
    public event EventHandler<int>? DpiChanged2;

    public ChromeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;
        Padding = Padding.Empty;
    }

    protected override void WndProc(ref Message m)
    {
        // DPI 变化必须最先处理（改变客户区计算基础）。
        if (m.Msg == WM_DPICHANGED)
        {
            var newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            SetBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            DpiChanged2?.Invoke(this, newDpi);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                var screen = Screen.FromHandle(Handle);
                var b = screen.Bounds;
                var w = screen.WorkingArea;
                int left = Math.Abs(w.Left - b.Left);
                int top = Math.Abs(w.Top - b.Top);
                int right = Math.Abs(b.Right - w.Right);
                int bottom = Math.Abs(b.Bottom - w.Bottom);
                var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                p.rgrc0.Left += left;
                p.rgrc0.Top += top;
                p.rgrc0.Right -= right;
                p.rgrc0.Bottom -= bottom;
                Marshal.StructureToPtr(p, m.LParam, false);
            }
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result != (IntPtr)HTCLIENT) return; // 子控件已处理

            var screenPt = new Point(
                (short)(m.LParam.ToInt64() & 0xFFFF),
                (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
            var p = PointToClient(screenPt);

            bool left = p.X <= ResizeBorder;
            bool right = p.X >= ClientSize.Width - ResizeBorder;
            bool top = p.Y <= ResizeBorder;
            bool bottom = p.Y >= ClientSize.Height - ResizeBorder;

            if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
            if (left) { m.Result = (IntPtr)HTLEFT; return; }
            if (right) { m.Result = (IntPtr)HTRIGHT; return; }
            if (top) { m.Result = (IntPtr)HTTOP; return; }
            if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

            // 标题栏拖拽区（右侧 200px 留给窗口按钮）。
            if (p.Y <= TitleBarHeight && p.X < ClientSize.Width - TitleBarButtonZoneWidth)
            {
                m.Result = (IntPtr)HTCAPTION;
                return;
            }

            foreach (var reg in _webDragRegions)
            {
                if (reg.Contains(p)) { m.Result = (IntPtr)HTCAPTION; return; }
            }

            m.Result = (IntPtr)HTCLIENT;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>由 DragDropHandler 在 OnDraggableRegionsChanged 时调用（设备像素→逻辑像素）。</summary>
    public void SetWebDraggableRegions(IList<CefSharp.DraggableRegion> regions)
    {
        using var g = CreateGraphics();
        var scale = g.DpiX / 96f;
        _webDragRegions = regions
            .Where(r => r.Draggable)
            .Select(r => new Rectangle(
                (int)(r.X / scale),
                (int)(r.Y / scale),
                (int)(r.Width / scale),
                (int)(r.Height / scale)))
            .ToArray();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TrySetDwmAttribute(33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, 2 /* DWMWCP_ROUND */);
    }

    private void TrySetDwmAttribute(int attr, int value)
    {
        if (Environment.OSVersion.Version.Major < 10) return;
        try { DwmSetWindowAttribute(Handle, attr, ref value, sizeof(int)); }
        catch { /* Win7 或 dwmapi 缺失，忽略 */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);
}
