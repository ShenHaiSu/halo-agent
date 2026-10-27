using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>
/// AI 忙碌时点关闭的三选一对话框。不用 MessageBox（按钮文字无法改）。
/// 回车默认“取消”（安全默认，防误触退出）。
/// </summary>
public sealed class BusyExitDialog : Form
{
    public BusyExitDialog()
    {
        Text = "HaloAgent";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 150);

        var icon = new Label
        {
            Text = "⚠",
            Font = new Font("Segoe UI Symbol", 20f),
            ForeColor = Color.FromArgb(240, 180, 60),
            AutoSize = false,
            Bounds = new Rectangle(18, 18, 40, 40),
        };

        var msg = new Label
        {
            Text = "AI 正在执行任务，退出会中断。",
            AutoSize = false,
            Bounds = new Rectangle(64, 20, 340, 24),
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };

        var hint = new Label
        {
            Text = "已修改的文件可以做「撤销」，但正在跑的命令会被终止。",
            ForeColor = Color.Gray,
            AutoSize = false,
            Bounds = new Rectangle(64, 46, 340, 40),
        };

        var btnExit = MakeButton("继续退出", 64, 100, DialogResult.Yes);
        var btnTray = MakeButton("最小化到托盘", 168, 100, DialogResult.No);
        var btnCancel = MakeButton("取消", 290, 100, DialogResult.Cancel);

        Controls.AddRange(new Control[] { icon, msg, hint, btnExit, btnTray, btnCancel });
        AcceptButton = btnCancel;
        CancelButton = btnCancel;
    }

    private static Button MakeButton(string text, int x, int y, DialogResult dr) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, 100, 30),
        DialogResult = dr,
    };
}
