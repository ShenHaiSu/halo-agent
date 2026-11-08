using System.Collections.Concurrent;

namespace HaloAgent.Engine.Abstractions;

/// <summary>
/// In-memory truth held by the engine: session table, turn table,
/// per-turn cancellation tokens and a config snapshot.
/// Locks are layered: frame write lock outside, repository locks inside;
/// handlers must never acquire them in reverse order.
/// </summary>
public sealed class EngineState
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnTokens =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, ResponseFrame> _echoCache = new();
    private readonly Queue<long> _echoOrder = new();
    private readonly object _echoGate = new();
    private const int EchoCacheBound = 5000;

    public string? ActiveSessionId { get; set; }
    public string Mode { get; set; } = "normal";
    public string PersonaId { get; set; } = "default";
    public int MaxTurns { get; set; } = 10;

    public bool TryRememberSession(string sessionId, string title)
    {
        _sessions[sessionId] = title ?? string.Empty;
        return true;
    }

    public IReadOnlyDictionary<string, string> Sessions => _sessions;

    public void TrackTurn(string turnId, string sessionId) => _turns[turnId] = sessionId;

    public bool TryTakeTurnSession(string turnId, out string sessionId) =>
        _turns.TryGetValue(turnId, out sessionId!);

    public CancellationTokenSource EnsureTurnScope(string turnId)
    {
        return _turnTokens.GetOrAdd(turnId, _ => new CancellationTokenSource());
    }

    public CancellationToken TurnToken(string turnId) => EnsureTurnScope(turnId).Token;

    /// <summary>
    /// Fastest path: only signals the per-turn token, never takes the frame write lock.
    /// </summary>
    public bool TryCancelTurn(string turnId)
    {
        if (_turnTokens.TryGetValue(turnId, out var scope))
        {
            try
            {
                scope.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            return true;
        }
        return false;
    }

    public void ForgetTurn(string turnId)
    {
        _turnTokens.TryRemove(turnId, out _);
        _turns.TryRemove(turnId, out _);
    }

    /// <summary>Bounded idempotency echo: same request id replays the stored reply.</summary>
    public bool TryGetEcho(long requestId, out ResponseFrame? cached) =>
        _echoCache.TryGetValue(requestId, out cached);

    public void StoreEcho(long requestId, ResponseFrame response)
    {
        lock (_echoGate)
        {
            if (_echoCache.Count >= EchoCacheBound && _echoOrder.Count > 0)
            {
                long oldest = _echoOrder.Dequeue();
                _echoCache.TryRemove(oldest, out _);
            }
            if (_echoCache.TryAdd(requestId, response))
            {
                _echoOrder.Enqueue(requestId);
            }
        }
    }
}
