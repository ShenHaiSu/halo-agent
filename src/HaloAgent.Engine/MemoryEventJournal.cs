using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Bounded in-memory event journal: single-writer sequence, cap 4096,
/// single batch reads without I/O. Overflow drops the oldest and reports a gap.
/// </summary>
public sealed class MemoryEventJournal : IEventJournal
{
    public const int DefaultCapacity = 4096;
    public const int MaxBatch = 500;

    private readonly object _gate = new();
    private readonly LinkedList<EventFrame> _frames = new();
    private long _sequence;
    private long _droppedTotal;

    public MemoryEventJournal(int capacity = DefaultCapacity)
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
                return _sequence;
            }
        }
    }

    public long Append(string kind, object? payload = null)
    {
        JsonElement element = payload is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonSerializer.SerializeToElement(payload, HaloJson.Options);
        lock (_gate)
        {
            _sequence += 1;
            _frames.AddLast(new EventFrame(_sequence, kind, element));
            while (_frames.Count > Capacity)
            {
                _frames.RemoveFirst();
                _droppedTotal += 1;
            }
            return _sequence;
        }
    }

    public EventBatch ReadSince(long sinceSequence, int maxCount)
    {
        int take = Math.Clamp(maxCount <= 0 ? MaxBatch : maxCount, 1, MaxBatch);
        lock (_gate)
        {
            var list = new List<EventFrame>(take);
            bool hasGap = false;
            int droppedEstimate = 0;
            if (_frames.Count > 0)
            {
                long oldest = _frames.First!.Value.Sequence;
                if (sinceSequence < oldest - 1)
                {
                    hasGap = true;
                    droppedEstimate = (int)
                        Math.Min(int.MaxValue, oldest - 1 - sinceSequence + _droppedTotal);
                }
            }
            else if (sinceSequence < _sequence)
            {
                hasGap = true;
                droppedEstimate = (int)Math.Min(int.MaxValue, _sequence - sinceSequence);
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

            long next = list.Count > 0 ? list[^1].Sequence : Math.Max(sinceSequence, _sequence);
            return new EventBatch(list, next, hasGap, droppedEstimate);
        }
    }
}
