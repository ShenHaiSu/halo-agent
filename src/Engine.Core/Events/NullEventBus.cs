using HaloAgent.Engine.Abstractions.Frames;

namespace HaloAgent.Engine.Core.Events;

/// <summary>
/// 空事件总线（无头模式用）。
/// 所有 Publish 都是先走内存队列；Start() 后由写循环直接丢进 Noop 通道。
/// 无头模式下没有 UI 通道，但仍需要消费队列，否则队满会反压阻塞 agent 循环。
/// </summary>
public sealed class NullEventBus
{
    private EventBus? _adapter;

    public static NullEventBus Create() => new();

    public EventBus Adapter
    {
        get
        {
            _adapter ??= new EventBus(
                NoopChannel.Instance,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            return _adapter;
        }
    }

    private sealed class NoopChannel : IFrameChannel
    {
        public static readonly NoopChannel Instance = new();

        public string Describe => "noop";

        public ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct)
            => new(Task.FromResult(ReceiveResult.Closed()));

        public ValueTask SendAsync(Frame frame, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
