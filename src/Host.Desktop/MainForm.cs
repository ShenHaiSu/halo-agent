using System.Windows.Forms;
using Microsoft.Win32;

using CefSharp;
using CefSharp.WinForms;
using HaloAgent.Desktop.Bridge;
using HaloAgent.Desktop.Browser;
using HaloAgent.Desktop.Engine;
using HaloAgent.Desktop.Handlers;
using HaloAgent.Desktop.Shell;

namespace HaloAgent.Desktop;

/// <summary>
/// 主窗体（ChromeForm 无边框基类）。
/// 浏览器填满客户区；桥接注册 haloHost(11) + haloAgent(3) + hostBridge(兼容)；
/// 引擎异步启动；托盘/热键/单实例/关闭确认/崩溃恢复/位置持久化。
/// </summary>
public sealed class MainForm : ChromeForm, ITrayHost, ShutdownSequence.IShutdownTarget, HaloHostBridge.IDevToolsHost
{
    private const int WM_HOTKEY = 0x0312;

    private readonly HostOptions _opts;
    private readonly string _gpuMode;
    private readonly SingleInstance? _single;

    private ChromiumWebBrowser? _browser;
    private readonly HostBridge _compatBridge;
    private readonly HaloHostBridge _haloHost;
    private readonly AgentBridge _agentBridge;
    private readonly HostFileDialogs _dialogs;
    private EngineSupervisor? _engine;
    private TrayIcon? _trayIcon;
    private GlobalHotkeys? _hotkeys;
    private Panel? _banner;

    private bool _allowClose;
    private bool _isShuttingDown;
    private volatile bool _isBusy;

    private readonly object _throttleLock = new();
    private DateTime _lastPush = DateTime.MinValue;

    /// <summary>P3 主入口。</summary>
    public MainForm(HostOptions opts, EngineSupervisor? engine, string gpuMode, SingleInstance? single = null)
    {
        _opts = opts;
        _engine = engine;
        _gpuMode = gpuMode;
        _single = single;

        Text = Program.ProductName;
        MinimumSize = new Size(880, 560);
        ClientSize = new Size(1200, 820);
        BackColor = Color.FromArgb(24, 24, 27); // 深色底，防白闪
        TitleBarHeight = 40;
        KeyPreview = true;

        // 位置恢复。
        var placement = _opts.ResetWindow ? new WindowPlacement() : WindowPlacementStore.Load();
        try
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = _opts.ResetWindow
                ? new Rectangle(120, 80, 1200, 820)
                : WindowPlacementStore.ClampToVisibleScreen(placement.ToRectangle());
            if (!_opts.ResetWindow && placement.Maximized)
                WindowState = FormWindowState.Maximized;
        }
        catch { StartPosition = FormStartPosition.CenterScreen; }

        _dialogs = new HostFileDialogs(() => this);
        _compatBridge = new HostBridge(this, new EngineClient());
        if (engine is not null) _compatBridge.SetSupervisor(engine);
        _haloHost = new HaloHostBridge(() => this, _opts, () => _engine, InvokeScriptSafe, _gpuMode);
        _agentBridge = new AgentBridge(() => _engine?.TryGetClient(), _dialogs, SetBusy);

        CreateBrowser();

        if (_single is not null)
        {
            _single.ActivateRequested += OnActivateRequested;
            _single.StartListening();
        }
        SetupHotkeys();

        if (_engine is not null)
            _engine.StatusChanged += (_, s) => _ = _haloHost.EmitEngineStatusAsync(s);

        AppLog.Info($"主窗口已创建，尺寸 {Width}×{Height} @{Left},{Top}");
        try { Microsoft.Win32.SystemEvents.SessionEnding += OnSystemSessionEnding; }
        catch { /* 系统事件订阅失败不影响主流程 */ }
    }

    // ============================================================
    //  浏览器
    // ============================================================

    private void CreateBrowser()
    {
        var url = BrowserBootstrap.ResolveStartUrl(_opts);
        AppLog.Info($"浏览器起始地址：{url}");
        if (!_opts.IsDev)
        {
            var index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            AppLog.Info($"wwwroot/index.html 存在={File.Exists(index)}（{index}）");
        }
        _browser = new ChromiumWebBrowser(url) { Dock = DockStyle.Fill };

        _browser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = false;
        // 109 的 Register 无 isAsync 参数：异步靠 CefSharpSettings.ConcurrentTaskExecution（Program 已开）。
        _browser.JavascriptObjectRepository.Register("haloHost", _haloHost, options: BindingOptions.DefaultBinder);
        _browser.JavascriptObjectRepository.Register("haloAgent", _agentBridge, options: BindingOptions.DefaultBinder);
        _browser.JavascriptObjectRepository.Register("hostBridge", _compatBridge, options: BindingOptions.DefaultBinder);
        _browser.DragHandler = new DragDropHandler(paths => _ = _haloHost.EmitFilesDroppedAsync(paths));
        _browser.RequestHandler = new CrashRecoveryHandler(msg => ShowFatalBanner(msg));
        _browser.LifeSpanHandler = new HostLifeSpanHandler(() => _allowClose);
        _browser.DownloadHandler = new HostDownloadHandler();
        _browser.RenderProcessMessageHandler = new HostRenderProcessMessageHandler();

        _browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (_browser is null || !_browser.IsBrowserInitialized) return;
            AppLog.Info("浏览器已初始化");
            BeginInvoke(new Action(() =>
            {
                if (_opts.IsDev) _browser?.ShowDevTools();
                ScheduleWindowStatePush();
            }));
        };

        _browser.LoadError += (_, e) =>
        {
            AppLog.Error($"页面加载失败 {e.ErrorCode} {e.ErrorText} url={e.FailedUrl}");
            var dataUrl = FallbackErrorPage.Build(e.FailedUrl, e.ErrorCode.ToString(), e.ErrorText, AppLog.CurrentFile, _opts.IsDev);
            BeginInvoke(new Action(() => _browser?.Load(dataUrl)));
        };

        // 页面控制台直送日志：前端 JS 报错不再隐身，黑屏时先看日志里的 CONSOLE。
        _browser.ConsoleMessage += (_, e) =>
        {
            AppLog.Warn($"页面控制台 [{e.Source}:{e.Line}] {e.Message}");
        };

        _browser.LoadingStateChanged += (_, e) =>
        {
            AppLog.Info($"页面加载状态：IsLoading={e.IsLoading}");
            if (!e.IsLoading)
                ScheduleContentProbe();
        };

        Controls.Add(_browser);
    }

    /// <summary>
    /// 加载完成后探活：#app 仍为空说明 JS 没跑起来（资源缺失/语法不支持），
    /// 直接挂横幅报日志路径，绝不静默黑屏。
    /// </summary>
    private void ScheduleContentProbe()
    {
        var browser = _browser;
        if (browser is null) return;
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            try
            {
                browser.EvaluateScriptAsync(
                    "document.readyState + '|' + (function(){var el=document.getElementById('app');return el ? el.childElementCount : -1;})()")
                    .ContinueWith(t =>
                    {
                        try
                        {
                            var text = t.IsCompletedSuccessfully && t.Result is { Success: true } r
                                ? r.Result?.ToString() : null;
                            if (text is null) return;
                            AppLog.Info("页面探活：" + text);
                            var bar = text.IndexOf('|');
                            if (bar >= 0 && int.TryParse(text[(bar + 1)..], out var n) && n <= 0)
                                BeginInvoke(new Action(() => ShowFatalBanner(
                                    "界面空白：前端未渲染（可能是资源缺失）。日志：" + AppLog.CurrentFile)));
                        }
                        catch (Exception ex) { AppLog.Warn("页面探活失败: " + ex.Message); }
                    });
            }
            catch (Exception ex) { AppLog.Warn("页面探活失败: " + ex.Message); }
        });
    }

    /// <summary>前端事件推送通道（失败只记 WARN，不抛）。</summary>
    public void InvokeScriptSafe(string script)
    {
        if (_browser is null) return;
        try
        {
            _browser.EvaluateScriptAsync(script).ContinueWith(t =>
            {
                try
                {
                    if (t.IsCompletedSuccessfully && t.Result is { Success: false } r)
                        AppLog.Warn($"脚本执行失败：{r.Message}");
                }
                catch (Exception ex) { AppLog.Warn("事件推送失败: " + ex.Message); }
            });
        }
        catch (Exception ex) { AppLog.Warn("事件推送失败: " + ex.Message); }
    }

    public void ToggleDevTools()
    {
        if (InvokeRequired) { BeginInvoke(ToggleDevTools); return; }
        ToggleDevToolsInternal();
    }

    internal void ToggleDevToolsInternal()
    {
        try { _browser?.ShowDevTools(); }
        catch (Exception ex) { AppLog.Warn("打开 DevTools 失败: " + ex.Message); }
    }

    // ============================================================
    //  供桥接调用的窗口控制
    // ============================================================

    /// <summary>把动作切回 UI 线程（桥接跑在 CEF 后台线程）。</summary>
    public void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) Invoke(action);
        else action();
    }

    public void CloseForReal()
    {
        _allowClose = true;
        if (InvokeRequired) BeginInvoke(new Action(Close));
        else Close();
    }

    internal bool ConfirmQuitByUser() => true; // P5 改为询问前端；P3 由 BusyExitDialog 覆盖

    /// <summary>当前 GPU 决策（diagnostics/桥接 systemInfo 用）。</summary>
    public string CurrentGpuDecision => _gpuMode;

    public EngineSupervisor? Engine => _engine;

    public void SetBusy(bool busy) => _isBusy = busy;

    public WindowStateDto CurrentWindowState()
    {
        int dpi = 96;
        try { using var g = CreateGraphics(); dpi = (int)g.DpiX; } catch { /* GDI 失败时用 96 */ }
        return new WindowStateDto
        {
            Maximized = WindowState == FormWindowState.Maximized,
            Minimized = WindowState == FormWindowState.Minimized,
            Width = ClientSize.Width,
            Height = ClientSize.Height,
            Dpi = dpi,
            TitleBarHeight = TitleBarHeight,
        };
    }

    public void DoMinimize() => BeginInvoke(() => WindowState = FormWindowState.Minimized);

    public WindowStateDto DoMaximizeOrRestore()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
        return CurrentWindowState();
    }

    public void DoClose() => BeginInvoke(Close);

    public void ScheduleWindowStatePush()
    {
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPush).TotalMilliseconds < 16) return; // 60fps 上限
            _lastPush = now;
        }
        _ = _haloHost.EmitWindowStateAsync(CurrentWindowState());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_haloHost is not null) ScheduleWindowStatePush();
    }

    // ============================================================
    //  托盘 / 热键（ITrayHost）
    // ============================================================

    public void ActivateFromTray()
    {
        if (InvokeRequired) { BeginInvoke(ActivateFromTray); return; }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        TopMost = true; TopMost = false;
        NativeMethods.SetForegroundWindow(Handle);
    }

    public void HideToTray()
    {
        Hide();
        _trayIcon ??= new TrayIcon(this, AppIcons.Default.TrayIcon, new EmptyRecentSessions());
    }

    public void RequestExit() => BeginInvoke(Close);

    public void NewSessionFromTray()
    {
        ActivateFromTray();
        _ = _agentBridge.Request("session.new", null);
    }

    public void OpenSessionFromTray(string id)
    {
        ActivateFromTray();
        _ = _agentBridge.Request("session.resume", new { id });
    }

    public void OpenSettingsFromTray()
    {
        ActivateFromTray();
        InvokeScriptSafe("window.__haloHostEvent && window.__haloHostEvent('host.openSettings', {})");
    }

    public void ShowAboutFromTray()
    {
        ActivateFromTray();
        InvokeScriptSafe("window.__haloHostEvent && window.__haloHostEvent('host.showAbout', {})");
    }

    private void SetupHotkeys()
    {
        _hotkeys = new GlobalHotkeys(Handle);
        if (!_hotkeys.TryRegister(GlobalHotkeys.IdToggleWindow,
                Keys.Space, GlobalHotkeys.KeyModifiers.Control | GlobalHotkeys.KeyModifiers.Alt))
            AppLog.Warn("全局热键 Ctrl+Alt+Space 被占用");
        if (!_hotkeys.TryRegister(GlobalHotkeys.IdNewSession,
                Keys.N, GlobalHotkeys.KeyModifiers.Control | GlobalHotkeys.KeyModifiers.Shift))
            AppLog.Warn("全局热键 Ctrl+Shift+N 被占用");
    }

    private void OnActivateRequested(string[] args)
    {
        if (InvokeRequired) { BeginInvoke(() => OnActivateRequested(args)); return; }
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
        TopMost = true;
        TopMost = false;
        NativeMethods.SetForegroundWindow(Handle);
        if (args.Length > 0)
            _ = _haloHost.EmitFilesDroppedAsync(args);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case GlobalHotkeys.IdToggleWindow:
                    if (Visible && ContainsFocus) HideToTray();
                    else ActivateFromTray();
                    break;
                case GlobalHotkeys.IdNewSession:
                    ActivateFromTray();
                    NewSessionFromTray();
                    break;
            }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    // ============================================================
    //  错误横幅
    // ============================================================

    public void ShowFatalBanner(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowFatalBanner(text)); return; }
        if (_banner is not null) return;
        _banner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(120, 30, 30),
        };
        var lbl = new Label
        {
            Text = text,
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        };
        var close = new Button
        {
            Text = "×",
            Dock = DockStyle.Right,
            Width = 36,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => RemoveBanner();
        _banner.Controls.Add(lbl);
        _banner.Controls.Add(close);
        Controls.Add(_banner);
        _banner.BringToFront();
    }

    public void RemoveBanner()
    {
        if (InvokeRequired) { BeginInvoke(RemoveBanner); return; }
        if (_banner is null) return;
        Controls.Remove(_banner);
        _banner.Dispose();
        _banner = null;
    }

    // ============================================================
    //  关闭（ShutdownSequence.IShutdownTarget）
    // ============================================================

    public void SavePlacementNow()
    {
        if (_opts.ResetWindow) return;
        try
        {
            var p = new WindowPlacement();
            p.FromForm(this);
            WindowPlacementStore.Save(p);
        }
        catch { /* 保存失败不影响退出 */ }
    }

    public async Task StopEngineAsync(TimeSpan timeout)
    {
        if (_engine is null) return;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _engine.StopAsync().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 超时则继续 */ }
        catch (TimeoutException) { /* 超时则继续 */ }
    }

    public void DisposeHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
    }

    public void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    public void DisposeBrowser()
    {
        if (_browser is null) return;
        try { _browser.Dispose(); }
        catch (Exception ex) { AppLog.Warn("释放浏览器失败：" + ex.Message); }
        _browser = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isShuttingDown) { base.OnFormClosing(e); return; }

        if (e.CloseReason == CloseReason.UserClosing)
        {
            if (_isBusy)
            {
                using var dlg = new BusyExitDialog();
                switch (dlg.ShowDialog(this))
                {
                    case DialogResult.Cancel:
                        e.Cancel = true;
                        return;
                    case DialogResult.No:
                        e.Cancel = true;
                        HideToTray();
                        return;
                }
            }
        }

        var fast = e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall;
        _isShuttingDown = true;
        ShutdownSequence.Run(this, fast);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ShutdownSequence.Run(this, fast: true);
        if (_single is not null)
        {
            _single.ActivateRequested -= OnActivateRequested;
            _single.Dispose();
        }
        base.OnFormClosed(e);
    }

    private void OnSystemSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        AppLog.Info($"系统会话结束：{e.Reason}");
        _isShuttingDown = true;
        ShutdownSequence.Run(this, fast: true);
    }

    protected override void Dispose(bool disposing)
    {
        try { Microsoft.Win32.SystemEvents.SessionEnding -= OnSystemSessionEnding; } catch { /* 取消订阅失败可忽略 */ }
        if (disposing)
        {
            try { _browser?.Dispose(); }
            catch (Exception ex) { AppLog.Warn("释放浏览器时异常（可忽略）: " + ex.Message); }
            _browser = null;
        }
        base.Dispose(disposing);
    }
}
