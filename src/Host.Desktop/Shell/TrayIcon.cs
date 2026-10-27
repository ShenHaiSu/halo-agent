using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>托盘对主窗口的最小依赖（避免 TrayIcon 直接依赖 MainForm 具体类）。</summary>
public interface ITrayHost
{
    void ActivateFromTray();
    void NewSessionFromTray();
    void OpenSessionFromTray(string id);
    void OpenSettingsFromTray();
    void ShowAboutFromTray();
    void RequestExit();
}

/// <summary>
/// 托盘图标与菜单。必须显式 Dispose（先 Visible=false 再 Dispose），否则退出后图标残留。
/// tooltip 限 63 字符。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ITrayHost _host;
    private readonly IRecentSessions _recent;
    private readonly ToolStripMenuItem _recentMenu;
    private readonly ToolStripMenuItem _engineStatusItem;

    public TrayIcon(ITrayHost host, Icon icon, IRecentSessions recent)
    {
        _host = host;
        _recent = recent;
        _recentMenu = new ToolStripMenuItem("最近的会话");
        _engineStatusItem = new ToolStripMenuItem("引擎状态：未知") { Enabled = false };
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "HaloAgent",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => _host.ActivateFromTray();
        RefreshRecentMenu();
    }

    public void SetStatus(string text)
    {
        _icon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    public void SetEngineStatus(string text)
    {
        _engineStatusItem.Text = "引擎状态：" + text;
        SetStatus("HaloAgent — " + text);
    }

    public void ShowFirstRunBalloon() => _icon.ShowBalloonTip(
        timeout: 5000,
        tipTitle: "HaloAgent 正在后台运行",
        tipText: "双击托盘图标可以随时把它叫回来。",
        tipIcon: ToolTipIcon.Info);

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => _host.ActivateFromTray());
        menu.Items.Add("新建会话", null, (_, _) => _host.NewSessionFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_recentMenu);
        menu.Items.Add(_engineStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => _host.OpenSettingsFromTray());
        menu.Items.Add("关于", null, (_, _) => _host.ShowAboutFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _host.RequestExit());
        return menu;
    }

    public void RefreshRecentMenu()
    {
        var items = _recent.List().Take(10).ToList();
        _recentMenu.DropDownItems.Clear();
        if (items.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("（暂无）") { Enabled = false });
            return;
        }
        foreach (var s in items)
        {
            var item = new ToolStripMenuItem(Truncate(s.Title, 40), null,
                (_, _) => _host.OpenSessionFromTray(s.Id));
            item.ToolTipText = $"{s.Title}\n{s.ModifiedAt:yyyy-MM-dd HH:mm}";
            _recentMenu.DropDownItems.Add(item);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public void Dispose()
    {
        // 顺序很重要：先 Visible=false，再 Dispose，否则图标残留到鼠标划过。
        _icon.Visible = false;
        _icon.Dispose();
    }
}
