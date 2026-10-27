namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 引擎守护：负责启动、监控、重连引擎进程。
///
/// ============ 状态机 ============
///
///   Idle ──Start()──▶ Starting ──成功──▶ Ready
///                        │                 │
///                        │失败              │进程退出/连接断
///                        ▼                 ▼
///                     Failed ◀──────── Recovering
///                                          │
///                                    退避后重试
///
/// 退避：200 → 400 → 800 → 1600 → 3200（封顶 5000）ms，最多 10 次
///
/// ============ 重连后的状态恢复 ============
///   1. 重新 state.snapshot 拿到全量状态
///   2. 前端用快照完全重建界面
///   3. 排队消息由宿主侧保留（不在引擎里），重连后重新 agent.queue
/// </summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly object _lock = new();
    private volatile EngineClient? _client;
    private CancellationTokenSource? _superviseCts;
    private int _attempt;
    private volatile SupervisedState _state = new(SupervisorPhase.Idle, null, null);

    /// <summary>排队消息（宿主侧保留，重连后重放）。</summary>
    private readonly List<string> _pendingQueue = new();

    public EngineSupervisor(string dataDir) => _dataDir = dataDir;

    public EngineClient Client => _client
        ?? throw new InvalidOperationException("引擎未就绪");

    /// <summary>未就绪时返回 null（桥接层做降级展示用，不抛异常）。</summary>
    public EngineClient? TryGetClient() => _client;

    public SupervisedState State => _state;

    public event Action<SupervisedState>? StateChanged;

    // ============================================================
    //  启动
    // ============================================================

    /// <summary>
    /// 启动守护（长驻：成功后阻塞在监控里，直到取消或连续失败 10 次）。
    /// 调用方应在后台跑它（`_ = supervisor.StartAsync()`），用 StateChanged 感知状态。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_superviseCts is not null)
                throw new InvalidOperationException("守护已在运行");
            _superviseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        await RecoverAsync(_superviseCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 手动重试（仅 Failed 态可用）。后台重跑恢复流程，立即返回；
    /// 成功与否都经 StateChanged 通知。
    /// </summary>
    public void RetryInBackground()
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_state.Phase != SupervisorPhase.Failed)
                throw new InvalidOperationException("引擎尚未进入失败态，无需手动重试");
            try { _superviseCts?.Cancel(); } catch { }
            _superviseCts?.Dispose();
            _superviseCts = new CancellationTokenSource();
            token = _superviseCts.Token;
        }
        _ = Task.Run(() => RecoverAsync(token));
    }

    // ============================================================
    //  恢复流程
    // ============================================================

    private async Task RecoverAsync(CancellationToken ct)
    {
        _attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(new SupervisedState(SupervisorPhase.Starting, null, null));

            try
            {
                var client = await StartOnceAsync(ct).ConfigureAwait(false);
                _client = client;
                _attempt = 0;

                // 重放排队消息
                await ReplayQueueAsync(client).ConfigureAwait(false);

                SetState(new SupervisedState(SupervisorPhase.Ready, null, null));
                AppLog.Info("引擎就绪");

                // 监控
                await MonitorAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("引擎启动/连接失败", ex);

                _attempt++;
                if (_attempt >= 10)
                {
                    SetState(new SupervisedState(
                        SupervisorPhase.Failed, ex.Message, WillRetry: false));
                    AppLog.Error($"引擎连续 {_attempt} 次启动失败，放弃重连");
                    break;
                }

                var delay = Math.Min(200 * (1 << Math.Min(_attempt - 1, 4)), 5000);
                SetState(new SupervisedState(
                    SupervisorPhase.Recovering,
                    $"第 {_attempt} 次重连失败，{delay}ms 后重试：{ex.Message}",
                    WillRetry: true));
                AppLog.Info($"引擎重连退避 {delay}ms（第 {_attempt} 次）");

                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
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

    /// <summary>
    /// 管道名：haloagent-{宿主PID}-{8位随机}
    ///
    /// 每次重连都重新生成：
    ///   - 旧管道可能被残留进程占用
    ///   - 随机后缀防猜测
    /// </summary>
    internal static string BuildPipeName()
    {
        var random = Guid.NewGuid().ToString("N")[..8];
        return $"haloagent-{Environment.ProcessId}-{random}";
    }

    /// <summary>监控：等进程退出或连接断开。</summary>
    private async Task MonitorAsync(EngineClient client, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExit(int code) => tcs.TrySetResult($"引擎进程退出，退出码 {code}");
        void OnLost(string reason) => tcs.TrySetResult($"连接断开：{reason}");

        client.ProcessExited += OnExit;
        client.ConnectionLost += OnLost;

        try
        {
            var completed = await Task.WhenAny(
                tcs.Task,
                Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);

            if (completed == tcs.Task)
            {
                var reason = await tcs.Task.ConfigureAwait(false);
                AppLog.Warn(reason);
                SetState(new SupervisedState(SupervisorPhase.Recovering, reason, WillRetry: true));

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

    // ============================================================
    //  排队消息（宿主侧保留）
    // ============================================================

    public void RememberQueued(string text)
    {
        lock (_lock) _pendingQueue.Add(text);
    }

    public void ForgetQueued(string text)
    {
        lock (_lock) _pendingQueue.Remove(text);
    }

    private async Task ReplayQueueAsync(EngineClient client)
    {
        List<string> copy;
        lock (_lock) copy = _pendingQueue.ToList();

        foreach (var text in copy)
        {
            try
            {
                await client.RequestAsync<object>("agent.queue", new { text })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"重放排队消息失败: {ex.Message}");
            }
        }
    }

    // ============================================================
    //  状态
    // ============================================================

    private void SetState(SupervisedState s)
    {
        _state = s;
        try { StateChanged?.Invoke(s); }
        catch (Exception ex) { AppLog.Warn("引擎状态订阅者异常: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        EngineClient? client;
        lock (_lock)
        {
            cts = _superviseCts; _superviseCts = null;
            client = _client; _client = null;
        }
        cts?.Cancel();
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        cts?.Dispose();
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
