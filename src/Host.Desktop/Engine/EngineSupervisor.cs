namespace HaloAgent.Desktop.Engine;

/// <summary>P3 引擎状态机：Starting → Ready ⇄ Restarting → Failed / Stopped。</summary>
public enum EngineState { Starting, Ready, Restarting, Failed, Stopped }

public sealed record EngineStatus(EngineState State, int? Pid = null, string? Reason = null);

/// <summary>
/// 引擎监督者：启动、健康监控、崩溃重启、受控关闭。
/// 自动重启上限 3 次、间隔 1s/2s/4s（确定性崩溃无限重启只会打满 CPU）。
/// 管道就绪等待 30s 超时。关闭时先停心跳再停进程。
/// 另保留 P1 兼容面（State/TryGetClient/RetryInBackground/排队重放/BuildPipeName）。
/// </summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private const int MaxAutoRestarts = 3;

    private readonly string _dataDir;
    private readonly string? _cwd;
    private readonly string? _logDir;
    private readonly string? _extraArgs;
    private readonly object _lock = new();
    private readonly List<string> _pendingQueue = new();
    private readonly CancellationTokenSource _cts = new();

    private EngineClient? _client;
    private EngineHealthMonitor? _monitor;
    private int _restartCount;
    private bool _stopping;
    private int _attemptCompat;

    public EngineSupervisor(string dataDir, string? cwd = null, string? logDir = null, string? extraArgs = null)
    {
        _dataDir = dataDir;
        _cwd = cwd;
        _logDir = logDir;
        _extraArgs = extraArgs;
        Status = new EngineStatus(EngineState.Stopped);
        State = new SupervisedState(SupervisorPhase.Idle, null, null);
    }

    // ---- P3 新面 ----
    public EngineStatus Status { get; private set; }
    public EngineClient? Client => _client;
    public event EventHandler<EngineStatus>? StatusChanged;

    // ---- P1 兼容面 ----
    public SupervisedState State { get; private set; }
    public event Action<SupervisedState>? LegacyStateChanged;
    public EngineClient? TryGetClient() => _client;
    public void RememberQueued(string text) { lock (_lock) _pendingQueue.Add(text); }
    public void ForgetQueued(string text) { lock (_lock) _pendingQueue.Remove(text); }

    internal static string BuildPipeName()
    {
        var random = Guid.NewGuid().ToString("N")[..8];
        return $"haloagent-{Environment.ProcessId}-{random}";
    }

    // ============================================================
    //  启动
    // ============================================================

    public async Task StartAsync() => await StartInternalAsync().ConfigureAwait(false);

    /// <summary>P1 兼容：长驻守护（退避 200→5000ms / 10 次）。保留给旧调用方；新代码用 StartAsync().</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _attemptCompat = 0;
        while (!linked.Token.IsCancellationRequested)
        {
            SetBoth(new EngineStatus(EngineState.Starting), new SupervisedState(SupervisorPhase.Starting, null, null));
            try
            {
                var client = await StartOnceAsync(linked.Token).ConfigureAwait(false);
                _client = client;
                _attemptCompat = 0;
                await ReplayQueueAsync(client).ConfigureAwait(false);
                SetBoth(new EngineStatus(EngineState.Ready, PidOf(client)), new SupervisedState(SupervisorPhase.Ready, null, null));
                AppLog.Info("引擎就绪");
                await MonitorCompatAsync(client, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("引擎启动/连接失败", ex);
                _attemptCompat++;
                if (_attemptCompat >= 10)
                {
                    SetBoth(new EngineStatus(EngineState.Failed, null, ex.Message),
                        new SupervisedState(SupervisorPhase.Failed, ex.Message, WillRetry: false));
                    break;
                }
                var delay = Math.Min(200 * (1 << Math.Min(_attemptCompat - 1, 4)), 5000);
                SetBoth(new EngineStatus(EngineState.Restarting, null, ex.Message),
                    new SupervisedState(SupervisorPhase.Recovering, $"第 {_attemptCompat} 次重连失败，{delay}ms 后重试：{ex.Message}", WillRetry: true));
                try { await Task.Delay(delay, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task StartInternalAsync()
    {
        SetStatus(new EngineStatus(EngineState.Starting));
        try
        {
            var pipeName = BuildPipeName();
            var client = new EngineClient();
            AppLog.Info($"引擎启动参数 dataDir={_dataDir} cwd={_cwd ?? "(默认)"} logDir={_logDir ?? "(默认)"}" + (_extraArgs is null ? "" : " extraArgs=已提供"));
            try
            {
                await client.StartAsync(pipeName, _dataDir, connectTimeoutMs: 30000).ConfigureAwait(false);
            }
            catch

            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _client = client;
            OrphanCleaner.WriteLock(ProcId(client) ?? -1, pipeName);

            _monitor = new EngineHealthMonitor(_client);
            _monitor.Unhealthy += OnEngineUnhealthy;
            _monitor.Start();

            client.ProcessExited += OnProcessExited;

            _restartCount = 0;
            SetStatus(new EngineStatus(EngineState.Ready, ProcId(client)));
            SetCompat(new SupervisedState(SupervisorPhase.Ready, null, null));
            AppLog.Info("引擎就绪");
        }
        catch (Exception ex)
        {
            AppLog.Error("启动引擎失败", ex);
            SetStatus(new EngineStatus(EngineState.Failed, null, ex.Message));
            SetCompat(new SupervisedState(SupervisorPhase.Failed, ex.Message, WillRetry: false));
        }
    }

    private async Task<EngineClient> StartOnceAsync(CancellationToken ct)
    {
        var pipeName = BuildPipeName();
        var client = new EngineClient();
        try
        {
            await client.StartAsync(pipeName, _dataDir).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MonitorCompatAsync(EngineClient client, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit(int code) => tcs.TrySetResult($"引擎进程退出，退出码 {code}");
        void OnLost(string reason) => tcs.TrySetResult($"连接断开：{reason}");
        client.ProcessExited += OnExit;
        client.ConnectionLost += OnLost;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (completed == tcs.Task)
            {
                var reason = await tcs.Task.ConfigureAwait(false);
                AppLog.Warn(reason);
                await client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            client.ProcessExited -= OnExit;
            client.ConnectionLost -= OnLost;
        }
    }

    private async Task ReplayQueueAsync(EngineClient client)
    {
        List<string> copy;
        lock (_lock) copy = _pendingQueue.ToList();
        foreach (var text in copy)
        {
            try
            {
                await client.RequestAsync<object>("agent.queue", new { text }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"重放排队消息失败: {ex.Message}");
            }
        }
    }

    // ============================================================
    //  崩溃与重启（P3：心跳失败与 Exited 双重保护 + 3 次上限）
    // ============================================================

    private void OnProcessExited(int exitCode)
    {
        if (_stopping) return;
        AppLog.Error($"引擎异常退出，退出码 {exitCode}");
        _ = HandleCrashAsync($"引擎异常退出（代码 {exitCode}）");
    }

    private void OnEngineUnhealthy(object? sender, EventArgs e)
    {
        if (_stopping) return;
        AppLog.Error("引擎心跳连续失败，判定为卡死");
        _ = HandleCrashAsync("引擎无响应");
    }

    private async Task HandleCrashAsync(string reason)
    {
        if (_stopping) return;
        if (Status.State == EngineState.Restarting) return;

        SetStatus(new EngineStatus(EngineState.Restarting, null, reason));
        await DisposeMonitorAndClientAsync().ConfigureAwait(false);

        if (_restartCount >= MaxAutoRestarts)
        {
            AppLog.Error($"已自动重启 {_restartCount} 次，停止尝试");
            SetStatus(new EngineStatus(EngineState.Failed, null, $"{reason}。已自动重启 {MaxAutoRestarts} 次仍未恢复。"));
            SetCompat(new SupervisedState(SupervisorPhase.Failed, reason, WillRetry: false));
            return;
        }

        _restartCount++;
        var delay = TimeSpan.FromSeconds(1 << (_restartCount - 1)); // 1s / 2s / 4s
        AppLog.Info($"第 {_restartCount} 次自动重启，等待 {delay.TotalSeconds}s");
        try
        {
            await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        await StartInternalAsync().ConfigureAwait(false);
    }

    /// <summary>用户手动重启（设置页“修复”按钮 / HostBridge.RestartEngine）。</summary>
    public async Task RestartAsync()
    {
        _restartCount = 0;
        if (_client is not null) _client.ProcessExited -= OnProcessExited;
        await DisposeMonitorAndClientAsync().ConfigureAwait(false);
        await StartInternalAsync().ConfigureAwait(false);
    }

    /// <summary>P1 兼容：后台重试。</summary>
    public void RetryInBackground()
    {
        lock (_lock)
        {
            if (State.Phase != SupervisorPhase.Failed)
                throw new InvalidOperationException("引擎尚未进入失败态，无需手动重试");
        }
        _ = Task.Run(() => RestartAsync());
    }

    // ============================================================
    //  关闭（先停心跳再停进程，防“关闭中被判卡死又重启”）
    // ============================================================

    public async Task StopAsync()
    {
        _stopping = true;
        await StopInternalAsync().ConfigureAwait(false);
        SetStatus(new EngineStatus(EngineState.Stopped));
        SetCompat(new SupervisedState(SupervisorPhase.Idle, null, null));
    }

    private async Task StopInternalAsync()
    {
        if (_client is { IsConnected: true } c)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await c.RequestAsync<object>("system.shutdown", null, cts.Token).ConfigureAwait(false);
            }
            catch { /* 引擎可能先退了，正常 */ }
        }

        await DisposeMonitorAndClientAsync().ConfigureAwait(false);
    }

    private async Task DisposeMonitorAndClientAsync()
    {
        if (_monitor is not null)
        {
            _monitor.Unhealthy -= OnEngineUnhealthy;
            await _monitor.DisposeAsync().ConfigureAwait(false);
            _monitor = null;
        }
        if (_client is not null)
        {
            _client.ProcessExited -= OnProcessExited;
            var pid = ProcId(_client);
            await _client.DisposeAsync().ConfigureAwait(false);
            if (pid is not null) OrphanCleaner.RemoveLock(pid.Value);
            _client = null;
        }
    }

    private static int? ProcId(EngineClient c)
    {
        try
        {
            var f = typeof(EngineClient).GetField("_proc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var p = f?.GetValue(c) as System.Diagnostics.Process;
            return p?.Id;
        }
        catch { return null; }
    }

    private static int? PidOf(EngineClient c) => ProcId(c);

    private void SetStatus(EngineStatus s)
    {
        Status = s;
        AppLog.Info($"引擎状态 → {s.State}" + (s.Reason is null ? "" : $"（{s.Reason}）"));
        try { StatusChanged?.Invoke(this, s); } catch (Exception ex) { AppLog.Warn("引擎状态订阅者异常: " + ex.Message); }
    }

    private void SetCompat(SupervisedState s)
    {
        State = s;
        try { LegacyStateChanged?.Invoke(s); } catch (Exception ex) { AppLog.Warn("引擎状态订阅者异常: " + ex.Message); }
    }

    private void SetBoth(EngineStatus n, SupervisedState l)
    {
        Status = n;
        State = l;
        AppLog.Info($"引擎状态 → {n.State}" + (n.Reason is null ? "" : $"（{n.Reason}）"));
        try { StatusChanged?.Invoke(this, n); } catch (Exception ex) { AppLog.Warn("引擎状态订阅者异常: " + ex.Message); }
        try { LegacyStateChanged?.Invoke(l); } catch (Exception ex) { AppLog.Warn("引擎状态订阅者异常: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Cancel();
        _cts.Dispose();
    }
}

public enum SupervisorPhase
{
    Idle,
    Starting,
    Ready,
    Recovering,
    Failed,
}

public sealed record SupervisedState(
    SupervisorPhase Phase,
    string? Message,
    bool? WillRetry);
