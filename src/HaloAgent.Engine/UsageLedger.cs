using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>Usage truth: token and retry counters with a tick snapshot for the display layer.</summary>
public sealed class UsageLedger
{
    private readonly object _gate = new();
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedTokens;
    private long _retryCount;

    public void Record(long inputTokens, long outputTokens, long cachedTokens = 0, long retries = 0)
    {
        lock (_gate)
        {
            _inputTokens += Math.Max(0, inputTokens);
            _outputTokens += Math.Max(0, outputTokens);
            _cachedTokens += Math.Max(0, cachedTokens);
            _retryCount += Math.Max(0, retries);
        }
    }

    public UsageSnapshot Query()
    {
        lock (_gate)
        {
            return new UsageSnapshot(
                _inputTokens,
                _outputTokens,
                _cachedTokens,
                _retryCount,
                DateTimeOffset.UtcNow
            );
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _inputTokens = 0;
            _outputTokens = 0;
            _cachedTokens = 0;
            _retryCount = 0;
        }
    }
}
