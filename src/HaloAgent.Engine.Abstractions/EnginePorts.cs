namespace HaloAgent.Engine.Abstractions;

/// <summary>Object channel between callers and the frame server (in-process queue for now).</summary>
public interface IFrameChannel
{
    Task<RequestFrame?> ReceiveAsync(CancellationToken cancellationToken);
    Task SendAsync(ResponseFrame response, CancellationToken cancellationToken);
}

/// <summary>Handler for one contract method.</summary>
public delegate Task<ResponseFrame> FrameHandler(
    RequestFrame request,
    IEngineContext context,
    CancellationToken cancellationToken
);

/// <summary>Per-call services handed to every handler (state, events, cancellation).</summary>
public interface IEngineContext
{
    EngineState State { get; }
    IEventJournal Events { get; }
    IEngineLog Log { get; }
    CancellationToken TurnToken(string turnId);
    bool TryCancelTurn(string turnId);

    /// <summary>Language gateway handle; null keeps the synchronous echo fallback.</summary>
    IEngineGateway? Gateway { get; }

    /// <summary>Token budget handle; null skips the pre-round budget guard.</summary>
    IEngineBudget? Budget { get; }

    /// <summary>Planner loop tuning; never null.</summary>
    EngineTurnOptions TurnOptions { get; }

    /// <summary>Host storage root handed to later tool execution.</summary>
    string DataRoot { get; }

    /// <summary>Tool execution seam; null keeps the deferred placeholder.</summary>
    IEngineToolRunner? ToolRunner { get; }
}

/// <summary>Monotonic event journal with bounded memory and gap reporting.</summary>
public interface IEventJournal
{
    long LatestSequence { get; }
    int Capacity { get; }
    long Append(string kind, object? payload = null);
    EventBatch ReadSince(long sinceSequence, int maxCount);
}

/// <summary>One batched read from the journal.</summary>
public sealed record EventBatch(
    IReadOnlyList<EventFrame> Frames,
    long NextSequence,
    bool HasGap,
    int DroppedEstimate
);
