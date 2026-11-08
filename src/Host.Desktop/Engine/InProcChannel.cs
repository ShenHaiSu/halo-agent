using HaloAgent.Engine;
using HaloAgent.Engine.Abstractions;

namespace Host.Desktop.Engine;

/// <summary>
/// In-process link between the thin shell and the engine. Calls travel as
/// objects (no framing overhead inside the process); only the outer web
/// boundary serializes to JSON string batches. The channel owns the drain
/// cursor that forwards freshly produced engine events into the host-side
/// ring after every call, so the ring and the journal stay aligned without
/// a background thread. The interface shape for a future out-of-process
/// link is preserved on the abstractions side; this class is the in-process
/// specialization and intentionally stays concrete.
/// </summary>
public sealed class InProcChannel : IAsyncDisposable
{
    private readonly FrameServer _server;
    private readonly EventRingBuffer _ring;
    private long _drainedThrough;
    private int _disposed;

    public InProcChannel(FrameServer server, EventRingBuffer ring)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    public FrameServer Server => _server;

    public EventRingBuffer Ring => _ring;

    public async Task<ResponseFrame> InvokeAsync(
        RequestFrame request,
        CancellationToken cancellationToken = default
    )
    {
        if (_disposed == 1)
        {
            throw new ObjectDisposedException(nameof(InProcChannel));
        }
        ResponseFrame response = await _server
            .SubmitAsync(request, cancellationToken)
            .ConfigureAwait(false);
        DrainNewEvents();
        return response;
    }

    /// <summary>
    /// Forwards journal frames produced since the previous drain into the
    /// host-side ring. Both stores share the same monotonic numbering, so
    /// the copy is append-only and duplicate-safe.
    /// </summary>
    public void DrainNewEvents()
    {
        EventBatch batch = _server.ReadEvents(_drainedThrough, EventRingBuffer.MaxBatch);
        while (batch.Frames.Count > 0)
        {
            foreach (EventFrame frame in batch.Frames)
            {
                _ring.Append(frame);
                _drainedThrough = frame.Sequence;
            }
            if (batch.Frames.Count < EventRingBuffer.MaxBatch)
            {
                break;
            }
            batch = _server.ReadEvents(_drainedThrough, EventRingBuffer.MaxBatch);
        }
        if (batch.NextSequence > _drainedThrough && batch.Frames.Count == 0)
        {
            _drainedThrough = Math.Max(_drainedThrough, batch.NextSequence);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
