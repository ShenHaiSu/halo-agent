using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

internal sealed class EngineContext : IEngineContext
{
    public EngineContext(EngineState state, IEventJournal events, IEngineLog log)
    {
        State = state;
        Events = events;
        Log = log;
    }

    public EngineState State { get; }
    public IEventJournal Events { get; }
    public IEngineLog Log { get; }

    public CancellationToken TurnToken(string turnId) => State.TurnToken(turnId);

    public bool TryCancelTurn(string turnId) => State.TryCancelTurn(turnId);
}
