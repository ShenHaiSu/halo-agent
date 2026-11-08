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
