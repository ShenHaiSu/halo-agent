namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 引擎健康监控：10s 心跳 × 3 次失败判卡死。
/// 心跳必须由引擎监听线程响应（P2 已保证），不能在 agent 回合线程上，否则长任务被误杀。
/// </summary>
public sealed class EngineHealthMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<bool>> _ping;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _consecutiveFailures;

    public int FailureThreshold { get; init; } = 3;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public event EventHandler? Unhealthy;

    /// <summary>生产构造：用 EngineClient 的 system.ping。</summary>
    public EngineHealthMonitor(EngineClient client)
        : this(async ct =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var r = await client.RequestAsync<SystemPingResult>("system.ping", (object?)null, cts.Token).ConfigureAwait(false);
                return r?.Pong == true;
            }
            catch
            {
                return false;
            }
        })
    {
    }

    /// <summary>测试构造：注入 ping 函数。</summary>
    public EngineHealthMonitor(Func<CancellationToken, Task<bool>> ping) => _ping = ping;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var ok = await PingOnceAsync(ct).ConfigureAwait(false);
                if (ok)
                {
                    if (_consecutiveFailures > 0)
                        AppLog.Info($"引擎心跳恢复（此前连续失败 {_consecutiveFailures} 次）");
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    AppLog.Warn($"引擎心跳失败（{_consecutiveFailures}/{FailureThreshold}）");
                    if (_consecutiveFailures >= FailureThreshold)
                    {
                        AppLog.Error("引擎连续心跳失败，判定为卡死");
                        Unhealthy?.Invoke(this, EventArgs.Empty);
                        _consecutiveFailures = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 停止时正常 */ }
    }

    private async Task<bool> PingOnceAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PingTimeout);
            return await _ping(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
            try { await _loop.ConfigureAwait(false); } catch { /* 关闭时忽略 */ }
        _cts.Dispose();
    }

    private sealed record SystemPingResult(bool Pong);
}
