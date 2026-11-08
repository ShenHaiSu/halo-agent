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
    private bool _usedEstimated;
    private long _lastSeq;
    private long _maxTokens = 128000;
    private long _budgetVersion;
    private BudgetView? _cachedView;
    private DateTimeOffset _cachedAt;

    public void SetMaxTokens(long maxTokens)
    {
        lock (_gate)
        {
            _maxTokens = maxTokens <= 0 ? 128000 : maxTokens;
            _budgetVersion++;
        }
    }

    public void Record(long inputTokens, long outputTokens, long cachedTokens = 0, long retries = 0)
    {
        RecordInternal(inputTokens, outputTokens, cachedTokens, retries, estimated: false, seq: 0);
    }

    public void RecordEstimated(long inputTokens, long outputTokens)
    {
        RecordInternal(inputTokens, outputTokens, 0, 0, estimated: true, seq: 0);
    }

    public bool RecordOnce(long seq, long inputTokens, long outputTokens, long cachedTokens = 0)
    {
        lock (_gate)
        {
            if (seq > 0 && seq <= _lastSeq)
            {
                return false;
            }
            if (seq > 0)
            {
                _lastSeq = seq;
            }
            _inputTokens += Math.Max(0, inputTokens);
            _outputTokens += Math.Max(0, outputTokens);
            _cachedTokens += Math.Max(0, cachedTokens);
            _budgetVersion++;
            return true;
        }
    }

    private void RecordInternal(
        long input,
        long output,
        long cached,
        long retries,
        bool estimated,
        long seq
    )
    {
        lock (_gate)
        {
            if (seq > 0 && seq <= _lastSeq)
            {
                return;
            }
            if (seq > 0)
            {
                _lastSeq = seq;
            }
            _inputTokens += Math.Max(0, input);
            _outputTokens += Math.Max(0, output);
            _cachedTokens += Math.Max(0, cached);
            _retryCount += Math.Max(0, retries);
            if (estimated)
            {
                _usedEstimated = true;
            }
            _budgetVersion++;
        }
    }

    public void MarkRetry()
    {
        lock (_gate)
        {
            _retryCount++;
            _budgetVersion++;
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

    public BudgetView BudgetStatus()
    {
        lock (_gate)
        {
            if (_cachedView is not null && _budgetVersion == _cachedView.Version)
            {
                return _cachedView;
            }
            long used = _inputTokens + _outputTokens;
            long max = _maxTokens <= 0 ? 128000 : _maxTokens;
            double percentage = max <= 0 ? 0 : Math.Clamp((double)used / max * 100.0, 0, 100);
            string occupancy =
                percentage >= 92
                    ? "high"
                    : percentage >= 70
                        ? "elevated"
                        : "normal";
            var view = new BudgetView(
                _inputTokens,
                _outputTokens,
                _cachedTokens,
                max,
                used,
                percentage,
                occupancy,
                _usedEstimated,
                _retryCount,
                _budgetVersion
            );
            _cachedView = view;
            _cachedAt = DateTimeOffset.UtcNow;
            return view;
        }
    }

    public DateTimeOffset CachedAt()
    {
        lock (_gate)
        {
            return _cachedAt;
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
            _usedEstimated = false;
            _lastSeq = 0;
            _budgetVersion++;
        }
    }

    public sealed record BudgetView(
        long InputTokens,
        long OutputTokens,
        long CachedTokens,
        long MaxTokens,
        long UsedTokens,
        double UsagePercentage,
        string OccupancyState,
        bool UsedIsEstimated,
        long RetryCount,
        long Version
    );
}
