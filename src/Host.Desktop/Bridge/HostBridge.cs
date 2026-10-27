using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using CefSharp;
using HaloAgent.Desktop.Engine;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;

namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 暴露给前端 JS 的宿主能力（window.hostBridge）。
/// ============ 铁律（P1 契约评审红线） ============
/// 1. 所有方法**必须**返回 Task&lt;T&gt;，禁止同步阻塞。
///    同步方法会在 CEF 的 IO 线程上阻塞，直接导致界面卡死。
/// 2. 内部任何 UI 操作**必须**通过 MainForm.OnUi() 切回 UI 线程。
/// 3. 这里**只放**窗口/系统类能力。任何与 agent 语义相关的东西
///    （消息、工具事件、流式文本）一律走 Named Pipe，不许进这里。
/// 4. 返回值与参数一律 camelCase（由 HaloJson 的命名策略保证）。
/// ================================================
/// </summary>
public sealed class HostBridge
{
    private readonly MainForm _form;
    private readonly EngineClient? _engine;
    private EngineSupervisor? _supervisor;

    /// <summary>P1 主入口：宿主通过守护进程持有引擎，支持重连与状态查询。</summary>
    public HostBridge(MainForm form, EngineSupervisor supervisor)
    {
        _form = form;
        _supervisor = supervisor;
    }

    /// <summary>P0 兼容入口：直连 EngineClient（无重连）。MainForm/Program 暂用它，后续切到主入口。</summary>
    public HostBridge(MainForm form, EngineClient engine)
    {
        _form = form;
        _engine = engine;
    }

    /// <summary>后续接线：把守护进程交给桥（替代直连）。</summary>
    public void SetSupervisor(EngineSupervisor supervisor) => _supervisor = supervisor;

    /// <summary>当前可用的引擎客户端（守护优先，直连兜底）。</summary>
    private EngineClient? ActiveClient => _supervisor?.TryGetClient() ?? _engine;

    // ---------------- 系统信息 ----------------

    /// <summary>前端启动时自检：判断运行环境，决定是否走低配渲染路径。</summary>
    public Task<SystemInfoDto> GetSystemInfoAsync()
    {
        var info = new SystemInfoDto
        {
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsDescription = Environment.OSVersion.VersionString,
            IsWin7 = Program.IsWin7(),
            Is64BitOs = Environment.Is64BitOperatingSystem,
            Is64BitProcess = Environment.Is64BitProcess,
            DpiScale = GetDpiScale(),
            ProcessId = Environment.ProcessId,
            AppVersion = typeof(HostBridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            CefVersion = Cef.CefVersion ?? "",
            ChromiumVersion = Cef.ChromiumVersion ?? "",
            DataDir = EngineStartupDefaults.DataDir,
            LogFile = AppLog.CurrentFile,
        };
        return Task.FromResult(info);
    }

    private static double GetDpiScale()
    {
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return Math.Round(g.DpiX / 96.0, 2);
        }
        catch { return 1.0; }
    }

    // ---------------- 窗口控制 ----------------

    public Task<bool> MinimizeAsync()
    {
        _form.OnUi(() => _form.WindowState = FormWindowState.Minimized);
        return Task.FromResult(true);
    }

    public Task<bool> ToggleMaximizeAsync()
    {
        _form.OnUi(() =>
        {
            _form.WindowState = _form.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        });
        return Task.FromResult(true);
    }

    public Task<bool> SetTitleAsync(string title)
    {
        var safe = (title ?? "").Trim();
        if (safe.Length > 120) safe = safe[..120];
        _form.OnUi(() => _form.Text = string.IsNullOrEmpty(safe)
            ? Program.ProductName
            : $"{safe} — {Program.ProductName}");
        return Task.FromResult(true);
    }

    public Task<bool> CloseWindowAsync()
    {
        _form.OnUi(_form.CloseForReal);
        return Task.FromResult(true);
    }

    /// <summary>前端问"现在可以关吗"（比如有任务在跑）。返回 true = 允许关闭。</summary>
    public Task<bool> ConfirmQuitAsync(string message)
    {
        var ok = false;
        _form.OnUi(() =>
        {
            ok = MessageBox.Show(
                _form,
                string.IsNullOrWhiteSpace(message) ? "确定要退出 HaloAgent 吗？" : message,
                Program.ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        });
        return Task.FromResult(ok);
    }

    /// <summary>关闭流程里被 MainForm 调用（同步，因为 FormClosing 是同步事件）。</summary>
    internal bool ConfirmQuitByUser() => true; // P5 改为询问前端；P0 直接放行

    // ---------------- 系统集成 ----------------

    /// <summary>用系统默认程序打开链接。只允许 http/https，避免前端被注入了 file:// 或自定义协议。</summary>
    public Task<bool> OpenExternalAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Task.FromResult(false);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(false);

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开外部链接失败: " + ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <summary>原生目录选择器。前端用作"切换工作目录"。</summary>
    public Task<string?> PickDirectoryAsync(string? initial)
    {
        string? result = null;
        _form.OnUi(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择工作目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dlg.SelectedPath = initial;
            if (dlg.ShowDialog(_form) == DialogResult.OK) result = dlg.SelectedPath;
        });
        return Task.FromResult(result);
    }

    public Task<string> ReadClipboardAsync()
    {
        string text = "";
        _form.OnUi(() =>
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { }
        });
        return Task.FromResult(text);
    }

    public Task<bool> WriteClipboardAsync(string text)
    {
        _form.OnUi(() =>
        {
            try { Clipboard.SetText(text ?? ""); } catch { }
        });
        return Task.FromResult(true);
    }

    /// <summary>把主题等 UI 偏好落到宿主侧（P0 只记日志，P4 会写进设置文件）。</summary>
    public Task<bool> SaveUiPreferenceAsync(string key, string value)
    {
        AppLog.Info($"UI 偏好: {key}={value}");
        return Task.FromResult(true);
    }

    // ---------------- 引擎连通性（P0 专用调试入口） ----------------

    /// <summary>
    /// 前端 → 宿主 → 引擎的连通性测试。
    /// 注意：这是 P0 专用的调试入口。P1 起，前端不再通过 JS 桥访问引擎，
    /// 而是通过宿主的持久管道连接（见 P1 的 Transport 设计）。
    /// </summary>
    public async Task<EnginePingResult> EnginePingAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = ActiveClient ?? throw new InvalidOperationException("引擎未就绪");
            var pong = await client.RequestAsync<JsonElement>("system.ping", (object?)null);
            sw.Stop();
            return new EnginePingResult
            {
                Ok = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                Raw = pong.ToString(),
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new EnginePingResult
            {
                Ok = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Error = ex.Message,
            };
        }
    }

    // ---------------- 引擎透传（D1-4 宿主中转层） ----------------

    /// <summary>
    /// 通用引擎调用：前端透过宿主调任意引擎方法。
    /// paramsJson 是 JSON 字符串（可空）；返回 {ok,result} 或 {ok:false,error} 的 JSON 字符串。
    /// </summary>
    public async Task<string> InvokeAsync(string method, string paramsJson)
    {
        try
        {
            var client = ActiveClient ?? throw new InvalidOperationException("引擎未就绪");
            JsonElement? prms = string.IsNullOrWhiteSpace(paramsJson)
                ? null
                : JsonSerializer.Deserialize<JsonElement>(paramsJson, HaloJson.Options);
            var result = await client.RequestAsync<JsonElement>(method, prms).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, result }, HaloJson.Options);
        }
        catch (RpcException ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = new { code = ex.Code, message = ex.Message, hint = ex.Hint },
            }, HaloJson.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = new { code = "internal", message = ex.Message, hint = (string?)null },
            }, HaloJson.Options);
        }
    }

    /// <summary>
    /// 拉取自 sinceSeq 之后（seq > sinceSeq）的事件，最多 max 条。
    /// 返回 {frames,nextSeq,hasGap,droppedEstimate} 的 JSON 字符串；frames 用 HaloJson 序列化。
    /// </summary>
    public Task<string> GetEventBatchAsync(long sinceSeq, int max)
    {
        var client = ActiveClient;
        if (client is null)
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                frames = Array.Empty<object>(),
                nextSeq = sinceSeq,
                hasGap = false,
                droppedEstimate = 0L,
            }, HaloJson.Options));

        var r = client.PollEvents(sinceSeq, max <= 0 ? 500 : max);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            frames = r.Frames,
            nextSeq = r.NextSeq,
            hasGap = r.HasGap,
            droppedEstimate = r.DroppedEstimate,
        }, HaloJson.Options));
    }

    /// <summary>当前引擎连接状态（前端轮询或事件驱动）。</summary>
    public Task<EngineConnectionState> GetEngineStateAsync()
    {
        if (_supervisor is null)
            return Task.FromResult(new EngineConnectionState
            {
                Phase = "ready",
                WillRetry = false,
                EngineRunning = _engine?.IsRunning ?? false,
            });

        var s = _supervisor.State;
        return Task.FromResult(new EngineConnectionState
        {
            Phase = s.Phase.ToString().ToLowerInvariant(),
            Message = s.Message,
            WillRetry = s.WillRetry ?? false,
            EngineRunning = _supervisor.TryGetClient()?.IsRunning ?? false,
        });
    }

    /// <summary>手动重试（Failed 状态下用户点"重试"）。后台重跑恢复流程，立即返回。</summary>
    public Task<bool> RetryEngineAsync()
    {
        if (_supervisor is null) return Task.FromResult(false);
        try
        {
            _supervisor.RetryInBackground();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("手动重试引擎失败: " + ex.Message);
            return Task.FromResult(false);
        }
    }
}

/// <summary>返回给前端的引擎连接状态（字段全部 camelCase）。</summary>
public sealed class EngineConnectionState
{
    public string Phase { get; set; } = "idle";
    public string? Message { get; set; }
    public bool WillRetry { get; set; }
    public bool EngineRunning { get; set; }
}

/// <summary>返回给前端的系统信息（字段全部 camelCase）。</summary>
public sealed class SystemInfoDto
{
    public string OsVersion { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public bool IsWin7 { get; set; }
    public bool Is64BitOs { get; set; }
    public bool Is64BitProcess { get; set; }
    public double DpiScale { get; set; } = 1.0;
    public int ProcessId { get; set; }
    public string AppVersion { get; set; } = "";
    public string CefVersion { get; set; } = "";
    public string ChromiumVersion { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string LogFile { get; set; } = "";
}

public sealed class EnginePingResult
{
    public bool Ok { get; set; }
    public long ElapsedMs { get; set; }
    public string? Raw { get; set; }
    public string? Error { get; set; }
}
