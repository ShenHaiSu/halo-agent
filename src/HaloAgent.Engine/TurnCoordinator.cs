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
    private readonly ConcurrentDictionary<string, PendingGate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestTurns =
        new(StringComparer.Ordinal);
    private const int RequestEchoBound = 5000;
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
        RemoveGatesFor(turnId);
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
        JsonElement args,
        string ticketId = ""
    )
    {
        var gate = new PendingGate(
            new ConfirmationRequest(
                turnId,
                toolCallId ?? string.Empty,
                toolName,
                args,
                DateTimeOffset.UtcNow,
                ticketId ?? string.Empty
            )
        );
        _gates[GateKey(turnId, toolCallId)] = gate;
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with { Outcome = TurnOutcome.AwaitingConfirmation };
        }
    }

    public bool TryTakeConfirmation(string turnId, out ConfirmationRequest? pending)
    {
        var matches = GatesFor(turnId);
        if (matches.Count == 1)
        {
            pending = matches[0].Request;
            return true;
        }
        pending = null;
        return false;
    }

    public bool TryTakeConfirmation(
        string turnId,
        string? toolCallId,
        out ConfirmationRequest? pending
    )
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return TryTakeConfirmation(turnId, out pending);
        }
        if (_gates.TryGetValue(GateKey(turnId, toolCallId), out var gate))
        {
            pending = gate.Request;
            return true;
        }
        pending = null;
        return false;
    }

    public bool ResolveConfirmation(string turnId, bool approved)
    {
        var matches = GatesFor(turnId);
        if (matches.Count != 1)
        {
            return false;
        }
        return CompleteGate(matches[0], new ConfirmationAnswer(approved, null));
    }

    public bool ResolveConfirmation(
        string turnId,
        string? toolCallId,
        bool approved,
        string? answersJson = null
    )
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return ResolveConfirmation(turnId, approved);
        }
        if (!_gates.TryGetValue(GateKey(turnId, toolCallId), out var gate))
        {
            return false;
        }
        return CompleteGate(gate, new ConfirmationAnswer(approved, answersJson));
    }

    public async Task<ConfirmationAnswer?> WaitForAnswerAsync(
        string turnId,
        string toolCallId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        if (!_gates.TryGetValue(GateKey(turnId, toolCallId), out var gate))
        {
            return null;
        }
        try
        {
            Task<ConfirmationAnswer> wait = gate.Completion.Task;
            Task finished = await Task.WhenAny(wait, Task.Delay(timeout, cancellationToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(finished, wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _gates.TryRemove(
                    new KeyValuePair<string, PendingGate>(GateKey(turnId, toolCallId), gate)
                );
                return null;
            }
            return await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public int PendingCountFor(string turnId) => GatesFor(turnId).Count;

    public bool MarkCancelled(string turnId)
    {
        bool signaled = _state.TryCancelTurn(turnId);
        if (_turns.TryGetValue(turnId, out var current))
        {
            _turns[turnId] = current with { Outcome = TurnOutcome.Cancelled };
        }
        FailGatesFor(turnId, "cancelled");
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

    public bool TryGetRequestTurn(string requestKey, out string? turnId)
    {
        turnId = null;
        if (string.IsNullOrWhiteSpace(requestKey))
        {
            return false;
        }
        return _requestTurns.TryGetValue(requestKey.Trim(), out turnId);
    }

    public void RememberRequestTurn(string requestKey, string turnId)
    {
        if (string.IsNullOrWhiteSpace(requestKey) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }
        try
        {
            if (_requestTurns.Count >= RequestEchoBound)
            {
                _requestTurns.Clear();
            }
            _requestTurns[requestKey.Trim()] = turnId;
        }
        catch { }
    }

    private static string GateKey(string turnId, string? toolCallId) =>
        (turnId ?? string.Empty) + "\0" + (toolCallId ?? string.Empty).Trim();

    private List<PendingGate> GatesFor(string turnId)
    {
        string prefix = (turnId ?? string.Empty) + "\0";
        var matches = new List<PendingGate>();
        foreach (var entry in _gates)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                matches.Add(entry.Value);
            }
        }
        return matches;
    }

    private bool CompleteGate(PendingGate gate, ConfirmationAnswer answer)
    {
        bool removed = false;
        foreach (var entry in _gates)
        {
            if (ReferenceEquals(entry.Value, gate))
            {
                removed |= _gates.TryRemove(new KeyValuePair<string, PendingGate>(entry.Key, gate));
            }
        }
        if (!removed)
        {
            return false;
        }
        try
        {
            gate.Completion.TrySetResult(answer);
        }
        catch { }
        if (_turns.TryGetValue(gate.Request.TurnId, out var current))
        {
            _turns[gate.Request.TurnId] = current with
            {
                Outcome = answer.Approved ? TurnOutcome.Running : TurnOutcome.Cancelled
            };
        }
        return true;
    }

    private void RemoveGatesFor(string turnId)
    {
        string prefix = (turnId ?? string.Empty) + "\0";
        foreach (string key in _gates.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _gates.TryRemove(key, out _);
            }
        }
    }

    private void FailGatesFor(string turnId, string outcome)
    {
        foreach (PendingGate gate in GatesFor(turnId))
        {
            try
            {
                gate.Completion.TrySetResult(new ConfirmationAnswer(false, outcome));
            }
            catch { }
        }
        RemoveGatesFor(turnId);
    }

    private sealed class PendingGate
    {
        public PendingGate(ConfirmationRequest request)
        {
            Request = request;
        }

        public ConfirmationRequest Request { get; }

        public TaskCompletionSource<ConfirmationAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
