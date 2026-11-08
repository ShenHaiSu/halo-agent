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

    public IEngineGateway? Gateway { get; set; }

    public IEngineBudget? Budget { get; set; }

    public EngineTurnOptions TurnOptions { get; set; } = new();

    public string DataRoot { get; set; } = string.Empty;

    public IEngineToolRunner? ToolRunner { get; set; }

    public CancellationToken TurnToken(string turnId) => State.TurnToken(turnId);

    public bool TryCancelTurn(string turnId) => State.TryCancelTurn(turnId);
}
