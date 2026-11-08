namespace HaloAgent.Engine;

/// <summary>
/// Memory first-in first-out lane for deferred prompts.
/// One entry per session keeps ordering stable; drain emits the oldest entry
/// as a fresh prompt while the caller holds the frame write gate.
/// </summary>
public sealed class TurnQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<QueuedPrompt>> _lanes = new(StringComparer.Ordinal);

    public int Enqueue(
        string sessionId,
        string text,
        string? providerId,
        string? modelId,
        string workspaceId,
        string? requestId
    )
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(sessionId, out var lane))
            {
                lane = new Queue<QueuedPrompt>();
                _lanes[sessionId] = lane;
            }
            lane.Enqueue(
                new QueuedPrompt(
                    sessionId,
                    text,
                    providerId,
                    modelId,
                    workspaceId,
                    requestId,
                    DateTimeOffset.UtcNow
                )
            );
            return lane.Count;
        }
    }

    public bool TryPeek(string sessionId, out QueuedPrompt? head)
    {
        lock (_gate)
        {
            head = null;
            if (!_lanes.TryGetValue(sessionId, out var lane) || lane.Count == 0)
            {
                return false;
            }
            head = lane.Peek();
            return true;
        }
    }

    public bool TryDequeue(string sessionId, out QueuedPrompt? head)
    {
        lock (_gate)
        {
            head = null;
            if (!_lanes.TryGetValue(sessionId, out var lane) || lane.Count == 0)
            {
                return false;
            }
            head = lane.Dequeue();
            if (lane.Count == 0)
            {
                _lanes.Remove(sessionId);
            }
            return true;
        }
    }

    public bool TryRemove(string sessionId, string? requestId, out QueuedPrompt? removed)
    {
        lock (_gate)
        {
            removed = null;
            if (!_lanes.TryGetValue(sessionId, out var lane) || lane.Count == 0)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(requestId))
            {
                removed = lane.Dequeue();
                if (lane.Count == 0)
                {
                    _lanes.Remove(sessionId);
                }
                return true;
            }
            var kept = new Queue<QueuedPrompt>();
            bool dropped = false;
            while (lane.Count > 0)
            {
                var item = lane.Dequeue();
                if (
                    !dropped
                    && string.Equals(item.RequestId, requestId.Trim(), StringComparison.Ordinal)
                )
                {
                    removed = item;
                    dropped = true;
                    continue;
                }
                kept.Enqueue(item);
            }
            if (kept.Count == 0)
            {
                _lanes.Remove(sessionId);
            }
            else
            {
                _lanes[sessionId] = kept;
            }
            return dropped;
        }
    }

    public int Depth(string sessionId)
    {
        lock (_gate)
        {
            return _lanes.TryGetValue(sessionId, out var lane) ? lane.Count : 0;
        }
    }

    public int TotalDepth()
    {
        lock (_gate)
        {
            int total = 0;
            foreach (var lane in _lanes.Values)
            {
                total += lane.Count;
            }
            return total;
        }
    }

    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            _lanes.Remove(sessionId);
        }
    }

    public sealed record QueuedPrompt(
        string SessionId,
        string Text,
        string? ProviderId,
        string? ModelId,
        string WorkspaceId,
        string? RequestId,
        DateTimeOffset EnqueuedAt
    );
}
