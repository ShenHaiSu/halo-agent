using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using CefSharp;
using HaloAgent.Desktop.Engine;
using HaloAgent.Desktop.Shell;

namespace HaloAgent.Desktop.Bridge;

 /// <summary>
 /// 宿主业务桥接（window.haloHost，恰好 12 方法 + 3 事件）。
 /// 其中 windowDragStart 是自绘标题栏 B 方案的显式拖拽入口：
 /// CEF 铺满客户区后 WM_NCHITTEST 到不了窗体，前端在标题栏 mousedown 时直调本方法，
 /// 宿主用 ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN/HTCAPTION) 把拖拽交给系统。
 /// 铁律：①全返回 Task ②UI 操作切 UI 线程（BeginInvoke+TCS，不用 Invoke 防死锁）
 /// ③只做宿主能力 ④非法参数抛 ArgumentException。
 /// 事件走 EvaluateScriptAsync 调 window.__haloHostEvent；页面未就绪时积压 20 条，
 /// 前端首次调 SystemInfo() 即视为就绪并补推。
 /// </summary>
 /// <remarks>
 /// 铁律（dist 骨架屏根因修复）：凡返回 DTO 的方法一律返回 JSON 字符串
 ///（BridgeJson.Options 序列化，camelCase），前端 JSON.parse。
 /// DTO 绝不能直接过 CefSharp 桥（按 C# 原名返回，前端读不到）。
 /// 事件推送（Emit*）本就走 JSON 文本，不受影响。
 /// </remarks>
public sealed class HaloHostBridge
{
    private readonly Func<ChromeForm> _formAccessor;
    private readonly HostOptions _opts;
    private readonly Func<EngineSupervisor?> _engineAccessor;
    private readonly Action<string> _invokeScript;
    private readonly string _gpuMode;
    private readonly bool _isFirstRun;

    private readonly Queue<(string kind, object payload)> _pendingEvents = new();
    private const int MaxPendingEvents = 20;
    private bool _frontendReady;

    public HaloHostBridge(
        Func<ChromeForm> formAccessor,
        HostOptions opts,
        Func<EngineSupervisor?> engineAccessor,
        Action<string> invokeScript,
        string gpuMode)
    {
        _formAccessor = formAccessor;
        _opts = opts;
        _engineAccessor = engineAccessor;
        _invokeScript = invokeScript;
        _gpuMode = gpuMode;
        _isFirstRun = !File.Exists(Path.Combine(opts.DataDir, "config.json"));
    }

    // ---------- 窗口（5：最小化 / 最大化还原 / 关闭 / 状态查询 / 显式拖拽） ----------

    public Task WindowMinimize()
    {
        var f = _formAccessor();
        f.BeginInvoke(() => f.WindowState = FormWindowState.Minimized);
        return Task.CompletedTask;
    }

    /// <summary>返回 WindowStateDto 的 JSON 字符串（camelCase）。</summary>
    public Task<string> WindowMaximizeOrRestore()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
            try
            {
                f.WindowState = f.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
                 tcs.TrySetResult(JsonSerializer.Serialize(ReadWindowState(f), BridgeJson.Options));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task WindowClose()
    {
        var f = _formAccessor();
        f.BeginInvoke(() => f.Close());
        return Task.CompletedTask;
    }

    /// <summary>返回 WindowStateDto 的 JSON 字符串（camelCase）。</summary>
    public Task<string> WindowState()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
             try { tcs.TrySetResult(JsonSerializer.Serialize(ReadWindowState(f), BridgeJson.Options)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }
    /// <summary>
    /// 显式启动窗口拖拽（自绘标题栏 B 方案）。
    /// 前端在标题栏 mousedown（左键、非最大化、非按钮区）时调用；
    /// 最大化状态下直接返回（由前端双击走最大化/还原）。
    /// 实现：ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN/HTCAPTION)，
    /// 把移动/双击/Aero Snap 都交给系统，与原生标题栏行为一致。
    /// 注意：调用时左键必须仍处于按下状态（前端 mousedown 里同步直调，不 await），
    /// 否则 SendMessage 进不了移动模态循环，本次调用即为空操作（无害）。
    /// </summary>
    public Task WindowDragStart()
    {
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
            try
            {
                if (f.IsDisposed || !f.IsHandleCreated) return;
                if (f.WindowState == FormWindowState.Maximized) return;
                if (f.WindowState == FormWindowState.Minimized) return;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(
                    f.Handle,
                    NativeMethods.WM_NCLBUTTONDOWN,
                    (IntPtr)NativeMethods.HTCAPTION,
                    IntPtr.Zero);
            }
            catch (Exception ex) { AppLog.Warn("窗口拖拽启动失败：" + ex.Message); }
        });
        return Task.CompletedTask;
    }

    // ---------- 系统信息（1） ----------

    /// <summary>返回 HostSystemInfo 的 JSON 字符串（camelCase）。</summary>
    public Task<string> SystemInfo()
    {
        MarkFrontendReady();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
            try
            {
                int dpi = 96;
                try { using var g = f.CreateGraphics(); dpi = (int)g.DpiX; } catch { /* GDI 失败时用 96 */ }
                 tcs.TrySetResult(JsonSerializer.Serialize(new HostSystemInfo
                {
                    OsDescription = Environment.OSVersion.ToString(),
                    Is64BitOs = Environment.Is64BitOperatingSystem,
                    Is64BitProcess = Environment.Is64BitProcess,
                    IsWindows7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1,
                    CefVersion = Cef.ChromiumVersion,
                    HostVersion = typeof(HaloHostBridge).Assembly.GetName().Version?.ToString() ?? "?",
                    Dpi = dpi,
                    IsDev = _opts.IsDev,
                    IsFirstRun = _isFirstRun,
                    DataDir = _opts.DataDir,
                    Cwd = _opts.Cwd,
                    GpuMode = _gpuMode,
                    Screens = Screen.AllScreens.Select(s => new ScreenDto
                    {
                        DeviceName = s.DeviceName,
                        Bounds = new[] { s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height },
                        Primary = s.Primary,
                    }).ToArray(),
                 }, BridgeJson.Options));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // ---------- 系统交互（5） ----------

    public Task OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("只允许打开 http/https 链接");
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public Task ShowItemInFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("路径不存在", full);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = File.Exists(full) ? $"/select,\"{full}\"" : $"\"{full}\"",
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public Task CopyText(string text)
    {
        // CEF 剪贴板在 Win7 偶发失效，走 C# 兜底；Clipboard 是 STA，必须 UI 线程。
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text);
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task<string?> ReadClipboardImage()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = _formAccessor();
        f.BeginInvoke(() =>
        {
            try
            {
                if (!Clipboard.ContainsImage()) { tcs.TrySetResult(null); return; }
                using var img = Clipboard.GetImage();
                if (img is null) { tcs.TrySetResult(null); return; }
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                tcs.TrySetResult(Convert.ToBase64String(ms.ToArray()));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task ToggleDevTools()
    {
        if (!_opts.IsDev)
            throw new InvalidOperationException("仅开发模式可用");
        var f = _formAccessor();
        f.BeginInvoke(() => (f as IDevToolsHost)?.ToggleDevTools());
        return Task.CompletedTask;
    }

    // ---------- 引擎修复（1） ----------

    public async Task RestartEngine()
    {
        AppLog.Info("用户请求重启引擎");
        var eng = _engineAccessor()
            ?? throw new InvalidOperationException("引擎未初始化");
        await eng.RestartAsync().ConfigureAwait(false);
    }

    // ============================================================
    //  事件（C# push → window.__haloHostEvent）
    // ============================================================

    public Task EmitWindowStateAsync(WindowStateDto s) { Emit("host.windowState", s); return Task.CompletedTask; }
    public Task EmitEngineStatusAsync(EngineStatus st)
    {
        Emit("host.engineStatus", new EngineStatusDto
        {
            State = st.State.ToString().ToLowerInvariant(),
            Pid = st.Pid,
            Reason = st.Reason,
        });
        return Task.CompletedTask;
    }
    public Task EmitFilesDroppedAsync(string[] paths) { Emit("host.filesDropped", new { paths }); return Task.CompletedTask; }
    public Task EmitNoticeAsync(string text) { Emit("host.notice", new { text }); return Task.CompletedTask; }

    private void Emit(string kind, object payload)
    {
        lock (_pendingEvents)
        {
            if (!_frontendReady)
            {
                if (_pendingEvents.Count >= MaxPendingEvents)
                    _pendingEvents.Dequeue();
                _pendingEvents.Enqueue((kind, payload));
                return;
            }
        }
        PushToFrontend(kind, payload);
    }

    private void PushToFrontend(string kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload, BridgeJson.Options);
        // kind 是固定字面量，payload 已序列化，此处安全。
        var script = $"window.__haloHostEvent && window.__haloHostEvent({JsonSerializer.Serialize(kind)}, {json})";
        _invokeScript(script);
    }

    private void MarkFrontendReady()
    {
        List<(string kind, object payload)> backlog;
        lock (_pendingEvents)
        {
            if (_frontendReady) return;
            _frontendReady = true;
            backlog = _pendingEvents.ToList();
            _pendingEvents.Clear();
        }
        AppLog.Info($"前端已就绪，补推 {backlog.Count} 条事件");
        foreach (var (kind, payload) in backlog)
            PushToFrontend(kind, payload);
    }

    private static WindowStateDto ReadWindowState(ChromeForm f)
    {
        int dpi = 96;
        try { using var g = f.CreateGraphics(); dpi = (int)g.DpiX; } catch { /* GDI 失败时用 96 */ }
        return new WindowStateDto
        {
            Maximized = f.WindowState == FormWindowState.Maximized,
            Minimized = f.WindowState == FormWindowState.Minimized,
            Width = f.ClientSize.Width,
            Height = f.ClientSize.Height,
            Dpi = dpi,
            TitleBarHeight = f.TitleBarHeight,
        };
    }

    /// <summary>MainForm 实现此接口以支持 ToggleDevTools（避免桥接依赖具体窗体类）。</summary>
    public interface IDevToolsHost
    {
        void ToggleDevTools();
    }
}
