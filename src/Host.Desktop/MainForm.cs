using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;
using HaloAgent.Desktop.Bridge;
using HaloAgent.Desktop.Engine;
using HaloAgent.Desktop.Handlers;

namespace HaloAgent.Desktop;

/// <summary>
/// 主窗体。设计上做到"零自绘"：
///   - 只有一个 ChromiumWebBrowser，铺满客户区；
///   - 所有视觉由前端负责；
///   - 这里只处理窗口生命周期与桥接注册。
/// 之所以不自绘标题栏：Win7 上无边框窗口的拖拽/最大化/DPI 行为有大量坑，
/// 收益（好看一点）远小于风险（用户拖不动窗口）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ChromiumWebBrowser _browser;
    private readonly HostBridge _bridge;
    private readonly DesktopStartup _startup;

    /// <summary>真正退出（区分"用户点 X"与"程序要求退出"）</summary>
    private bool _allowClose;

    public MainForm(DesktopStartup startup, EngineClient engine)
    {
        _startup = startup;

        Text = Program.ProductName;
        // Win7 上 880x560 是安全下限，太小会把输入框挤没
        MinimumSize = new System.Drawing.Size(880, 560);
        ClientSize = new System.Drawing.Size(1200, 820);
        StartPosition = FormStartPosition.CenterScreen;
        // 减少 Win7 上的重绘闪烁
        DoubleBuffered = true;
        KeyPreview = true;

        _bridge = new HostBridge(this, engine);
        _browser = new ChromiumWebBrowser
        {
            Dock = DockStyle.Fill,
        };

        // ---- 绑定 JS 桥 ----
        // LegacyBindingEnabled = false：使用 CefSharp 的异步绑定（Async Binding），
        // 同步老绑定在 109 上已被标记废弃，且会在 UI 线程上做阻塞调用。
        _browser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = false;
        _browser.JavascriptObjectRepository.Register(
            "hostBridge",
            _bridge,
            options: BindingOptions.DefaultBinder);

        // ---- 生命周期 ----
        _browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (!_browser.IsBrowserInitialized) return;
            BeginInvoke(new Action(NavigateInitial));
        };

        _browser.LifeSpanHandler = new HostLifeSpanHandler(() => _allowClose);
        _browser.DownloadHandler = new HostDownloadHandler();
        _browser.RenderProcessMessageHandler = new HostRenderProcessMessageHandler();

        Controls.Add(_browser);

        FormClosing += OnFormClosing;
    }

    /// <summary>外部（JS 或脚本）要求退出时调用，跳过"确认关闭"。</summary>
    public void CloseForReal()
    {
        _allowClose = true;
        // 必须回到 UI 线程
        if (InvokeRequired) BeginInvoke(new Action(Close));
        else Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 用户点 X（UserClosing）：先问前端要不要拦（"正在跑任务，确定退出？"）
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            var ok = _bridge.ConfirmQuitByUser();
            if (!ok)
            {
                e.Cancel = true;
                return;
            }
        }
        _allowClose = true;
        AppLog.Info("主窗体关闭");
    }

    private void NavigateInitial()
    {
        var dev = _startup.DevUrl;
        if (!string.IsNullOrWhiteSpace(dev))
        {
            // 开发态：直接连 Vite dev server，热更新可用
            AppLog.Info($"开发态加载 {dev}");
            _browser.Load(dev);
            _browser.ShowDevTools();
            return;
        }

        var url = $"{Program.AppScheme}://{Program.AppHost}/index.html";
        AppLog.Info($"生产态加载 {url}");
        _browser.Load(url);

        // 前端加载失败（wwwroot 缺失 / 白屏）时给出可操作提示，而不是让用户看白板
        _browser.LoadError += (_, e) =>
        {
            AppLog.Error($"页面加载失败 {e.ErrorCode} {e.ErrorText} url={e.FailedUrl}");
            var html = $@"<!doctype html><meta charset=""utf-8"">
<body style=""font-family:'Microsoft YaHei',sans-serif;background:#11151c;color:#e6ebf2;padding:40px"">
<h2>页面加载失败</h2>
<p>错误码：{e.ErrorCode}（{e.ErrorText}）</p>
<p>地址：{System.Net.WebUtility.HtmlEncode(e.FailedUrl)}</p>
<hr>
<p>排查：</p>
<ol>
  <li>确认安装目录下存在 <code>wwwroot\index.html</code></li>
  <li>若刚重装，重新运行安装包</li>
  <li>日志：{System.Net.WebUtility.HtmlEncode(AppLog.CurrentFile)}</li>
</ol>
</body>";
            var dataUrl = "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
            BeginInvoke(new Action(() => _browser.Load(dataUrl)));
        };
    }

    // ---- 供 HostBridge 调用的 UI 线程辅助 ----

    /// <summary>把动作切回 UI 线程。桥接方法跑在 CEF 的后台线程上，直接操作控件会抛跨线程异常。</summary>
    public void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) Invoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _browser.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("释放浏览器时异常（可忽略）: " + ex.Message);
            }
        }
        base.Dispose(disposing);
    }
}
