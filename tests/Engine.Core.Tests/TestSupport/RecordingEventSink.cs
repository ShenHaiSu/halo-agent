using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;

namespace HaloAgent.Engine.Core.Tests.TestSupport;

/// <summary>
/// 记录全部事件的假通道：EventBus 的写出端直接落进内存，便于做序列断言。
/// 注意 EventBus 是异步写出的，断言前先用 AgentFixture.WaitForKindAsync 等待目标事件到达。
/// </summary>
public sealed class RecordingEventSink : IFrameChannel
{
    private readonly object _gate = new();
    private readonly List<RecordedEvent> _events = new();

    public sealed record RecordedEvent(long Seq, string Kind, JsonElement? Payload);

    public string Describe => "recording";

    public ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct) =>
        new(Task.FromResult(ReceiveResult.Closed()));

    public ValueTask SendAsync(Frame frame, CancellationToken ct)
    {
        if (frame is EventFrame ev)
        {
            lock (_gate)
            {
                _events.Add(new RecordedEvent(ev.Seq, ev.Kind, ev.Payload));
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string[] Kinds()
    {
        lock (_gate)
        {
            return _events.Select(e => e.Kind).ToArray();
        }
    }

    public IReadOnlyList<RecordedEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    public bool HasKind(string kind)
    {
        lock (_gate)
        {
            return _events.Any(e => e.Kind == kind);
        }
    }

    /// <summary>把指定 kind 的 delta 文本按到达顺序拼起来（text.delta / reasoning.delta 用）。</summary>
    public string ConcatTextOf(string kind)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var e in _events)
            {
                if (e.Kind != kind || e.Payload is null) continue;
                var el = (JsonElement)e.Payload;
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
                else if (el.TryGetProperty("Text", out var t2) && t2.ValueKind == JsonValueKind.String)
                    sb.Append(t2.GetString());
            }
        }

        return sb.ToString();
    }
}
