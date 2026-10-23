namespace Host.Desktop.LlmGateway;

/// <summary>P0-2 空闲看门狗：零事件流 180s 超时 abort + 排空，防双计费。</summary>
public sealed class StreamIdleWatchdog : IDisposable
{
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<CancellationToken, Task> _onStall;
    private DateTime _lastEventUtc = DateTime.UtcNow;
    private bool _disposed;
    private int _sawStart;

    public static TimeSpan ResolveTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("HALO_STREAM_IDLE_TIMEOUT_MS");
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(180);
        if (!long.TryParse(raw.Trim(), out long ms))
            return TimeSpan.FromSeconds(180);
        if (ms == 0)
            return Timeout.InfiniteTimeSpan;
        ms = Math.Max(30_000, ms);
        return TimeSpan.FromMilliseconds(ms);
    }

    public StreamIdleWatchdog(
        Func<CancellationToken, Task> onStall,
        TimeSpan? timeoutOverride = null
    )
    {
        _onStall = onStall;
        _idleTimeout = timeoutOverride ?? ResolveTimeout();
    }

    public void MarkEvent()
    {
        _lastEventUtc = DateTime.UtcNow;
        _sawStart = 1;
    }

    public bool SawStart => _sawStart == 1;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_idleTimeout == Timeout.InfiniteTimeSpan)
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, linked.Token).ConfigureAwait(false);
                if (DateTime.UtcNow - _lastEventUtc >= _idleTimeout)
                {
                    await _onStall(linked.Token).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
            _cts.Dispose();
        }
    }
}
