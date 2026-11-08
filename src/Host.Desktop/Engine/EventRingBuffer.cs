using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace Host.Desktop.Engine;

/// <summary>
/// Host-side bounded copy of the engine event sequence, served to the web
/// client through the string-only boundary call. Capacity and single-batch
/// limits mirror the engine journal so overflow behavior stays predictable:
/// once the oldest retained frame moves past the requested cursor the read
/// reports a gap and the client rebuilds from a snapshot plus paged history.
/// Reads are pure memory access and never touch disk or the network, keeping
/// the bridge thread free for streaming workloads.
/// </summary>
public sealed class EventRingBuffer
{
    public const int DefaultCapacity = 4096;
    public const int MaxBatch = 500;

    private readonly object _gate = new();
    private readonly LinkedList<EventFrame> _frames = new();
    private long _droppedTotal;

    public EventRingBuffer(int capacity = DefaultCapacity)
    {
        Capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    public int Capacity { get; }

    public long LatestSequence
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count > 0 ? _frames.Last!.Value.Sequence : 0;
            }
        }
    }

    public int RetainedCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public long DroppedTotal
    {
        get
        {
            lock (_gate)
            {
                return _droppedTotal;
            }
        }
    }

    public void Append(EventFrame frame)
    {
        lock (_gate)
        {
            if (_frames.Count > 0 && frame.Sequence <= _frames.Last!.Value.Sequence)
            {
                return;
            }
            _frames.AddLast(frame);
            while (_frames.Count > Capacity)
            {
                _frames.RemoveFirst();
                _droppedTotal += 1;
            }
        }
    }

    public void Append(string kind, object? payload = null)
    {
        JsonElement element = payload is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonSerializer.SerializeToElement(payload, HaloJson.Options);
        lock (_gate)
        {
            long next = _frames.Count > 0 ? _frames.Last!.Value.Sequence + 1 : 1;
            _frames.AddLast(new EventFrame(next, kind, element));
            while (_frames.Count > Capacity)
            {
                _frames.RemoveFirst();
                _droppedTotal += 1;
            }
        }
    }

    /// <summary>
    /// Memory-only batched read. Never performs I/O; callers on the bridge
    /// thread stay unblocked even under sustained streaming load.
    /// </summary>
    public RingBatch Read(long sinceSequence, int maxCount)
    {
        int take = Math.Clamp(maxCount <= 0 ? MaxBatch : maxCount, 1, MaxBatch);
        lock (_gate)
        {
            var list = new List<EventFrame>(take);
            bool hasGap = false;
            long droppedEstimate = 0;
            if (_frames.Count > 0)
            {
                long oldest = _frames.First!.Value.Sequence;
                if (sinceSequence < oldest - 1)
                {
                    hasGap = true;
                    droppedEstimate = oldest - 1 - sinceSequence + _droppedTotal;
                }
            }

            foreach (var frame in _frames)
            {
                if (frame.Sequence > sinceSequence)
                {
                    list.Add(frame);
                    if (list.Count >= take)
                    {
                        break;
                    }
                }
            }

            long next =
                list.Count > 0
                    ? list[^1].Sequence
                    : Math.Max(sinceSequence, _frames.Count > 0 ? _frames.Last!.Value.Sequence : 0);
            return new RingBatch(list, next, hasGap, droppedEstimate);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _droppedTotal = 0;
        }
    }
}

/// <summary>One batched read from the host-side ring.</summary>
public sealed record RingBatch(
    IReadOnlyList<EventFrame> Frames,
    long NextSequence,
    bool HasGap,
    long DroppedEstimate
);
