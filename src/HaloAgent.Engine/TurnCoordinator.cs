using System.Collections.Concurrent;
using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Turn truth: one running turn per session, confirmation gate, fastest-path cancel.
/// Reentry reports the active request so the caller can wait instead of duplicating work.
/// Cancel only signals the per-turn token and never waits on IO or model calls.
/// </summary>
public sealed class TurnCoordinator
{
    private readonly ConcurrentDictionary<string, TurnRecord> _turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeBySession =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConfirmationRequest> _confirmations =
        new(StringComparer.Ordinal);
    private readonly EngineState _state;

    public TurnCoordinator(EngineState state)
    {
        _state = state;
    }

    public bool TryStart(
        string sessionId,
        string turnId,
        long requestId,
        out string activeRequestId
    )
    {
        activeRequestId = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        if (
            _activeBySession.TryGetValue(sessionId, out string? activeTurn)
            && activeTurn is not null
            && _turns.TryGetValue(activeTurn, out var active)
            && (
                active.Outcome == TurnOutcome.Running
                || active.Outcome == TurnOutcome.AwaitingConfirmation
            )
        )
        {
            activeRequestId = active.RequestId.ToString();
            return false;
        }
        var record = new TurnRecord(
            turnId,
            sessionId,
            requestId,
            TurnOutcome.Running,
            DateTimeOffset.UtcNow
        );
        _turns[turnId] = record;
        _activeBySession[sessionId] = turnId;
        _state.TrackTurn(turnId, sessionId);
        _state.EnsureTurnScope(turnId);
        return true;
    }

    public void Finish(string turnId, TurnOutcome outcome)
    {
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with { Outcome = outcome };
        }
        _confirmations.TryRemove(turnId, out _);
        if (
            TrySessionOf(turnId, out string? sessionId)
            && sessionId is not null
            && _activeBySession.TryGetValue(sessionId, out string? active)
            && string.Equals(active, turnId, StringComparison.Ordinal)
        )
        {
            _activeBySession.TryRemove(sessionId, out _);
        }
    }

    public bool TrySessionOf(string turnId, out string? sessionId)
    {
        if (_turns.TryGetValue(turnId, out var record))
        {
            sessionId = record.SessionId;
            return true;
        }
        sessionId = null;
        return false;
    }

    public bool IsActive(string turnId)
    {
        return _turns.TryGetValue(turnId, out var record)
            && (
                record.Outcome == TurnOutcome.Running
                || record.Outcome == TurnOutcome.AwaitingConfirmation
            );
    }

    public string? ActiveTurnOf(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }
        return _activeBySession.TryGetValue(sessionId, out string? turnId) ? turnId : null;
    }

    public void RaiseConfirmation(
        string turnId,
        string toolCallId,
        string toolName,
        JsonElement args
    )
    {
        _confirmations[turnId] = new ConfirmationRequest(
            turnId,
            toolCallId,
            toolName,
            args,
            DateTimeOffset.UtcNow
        );
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with { Outcome = TurnOutcome.AwaitingConfirmation };
        }
    }

    public bool TryTakeConfirmation(string turnId, out ConfirmationRequest? pending)
    {
        return _confirmations.TryGetValue(turnId, out pending);
    }

    public bool ResolveConfirmation(string turnId, bool approved)
    {
        if (!_confirmations.TryRemove(turnId, out _))
        {
            return false;
        }
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with
            {
                Outcome = approved ? TurnOutcome.Running : TurnOutcome.Cancelled
            };
        }
        return true;
    }

    public bool MarkCancelled(string turnId)
    {
        bool signaled = _state.TryCancelTurn(turnId);
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with { Outcome = TurnOutcome.Cancelled };
        }
        _confirmations.TryRemove(turnId, out _);
        if (
            TrySessionOf(turnId, out string? sessionId)
            && sessionId is not null
            && _activeBySession.TryGetValue(sessionId, out string? active)
            && string.Equals(active, turnId, StringComparison.Ordinal)
        )
        {
            _activeBySession.TryRemove(sessionId, out _);
        }
        return signaled;
    }
}
