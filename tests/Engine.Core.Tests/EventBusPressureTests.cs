using System.Diagnostics;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#if P1_BACKEND_EVENTBUS
using HaloAgent.Engine.Core.Events;
#endif

namespace HaloAgent.Engine.Core.Tests;

/// <summary>
/// 压力测试：模拟"模型疯狂输出 + 消费者慢"的极端场景，
/// 验证不 OOM、状态帧不丢、delta 丢弃可计数。
/// </summary>
public class EventBusPressureTests
{
    /// <summary>故意很慢的消费者，用来制造队满。</summary>
    private sealed class SlowChannel : IFrameChannel
    {
        public int Received;
        public readonly List<EventFrame> Frames = new();
        private readonly int _delayMs;

        public SlowChannel(int delayMs) => _delayMs = delayMs;

        public string Describe => "slow";
        public async ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct)
        {
            await Task.Delay(10, ct);
            return ReceiveResult.Closed();
        }

        public async ValueTask SendAsync(Frame frame, CancellationToken ct)
        {
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            if (frame is EventFrame ev)
            {
                Interlocked.Increment(ref Received);
                lock (Frames) Frames.Add(ev);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ============================================================
    //  以下 3 个用例需要后端 D1-3 的 EventBus
    //  （src/Engine.Core/Events/EventBus.cs，含 PublishDelta /
    //  PublishStateAsync / FlushNow / ConsumeDroppedCount / Start）。
    //  EventBus 落地后：定义编译符号 P1_BACKEND_EVENTBUS
    // （或直接删掉本文件的 #if / #endif），即可启用。
    // ============================================================

#if P1_BACKEND_EVENTBUS
    [Fact]
    public async Task HighFrequencyDeltas_do_not_grow_unbounded()
    {
        var slow = new SlowChannel(delayMs: 2);   // 每帧 2ms → 最多 500 帧/秒
        await using var bus = new EventBus(slow, NullLogger.Instance, capacity: 128);
        bus.Start();

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();

        // 模拟 10 秒的 120 帧/秒输出（1200 次 delta）
        for (int i = 0; i < 1200; i++)
        {
            bus.PublishDelta(EventKinds.TextDelta, "这是一个测试 token。");
            if (i % 10 == 0)
                await bus.PublishStateAsync(EventKinds.RoundStarted,
                    new RoundStartedPayload { Used = i, Limit = 200 }, default);
            await Task.Delay(8);   // ≈125 帧/秒
        }
        bus.FlushNow();
        await Task.Delay(1500);   // 等写出

        sw.Stop();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        var growthMb = (after - before) / 1024.0 / 1024.0;

        Assert.True(growthMb < 20, $"内存增长 {growthMb:0.0} MB，超过 20 MB 上限");
        Assert.True(sw.Elapsed.TotalSeconds < 30);

        // 状态帧必须全部到达
        var stateFrames = slow.Frames.Count(f => f.Kind == EventKinds.RoundStarted);
        Assert.Equal(120, stateFrames);
    }

    [Fact]
    public async Task Deltas_are_aggregated_not_one_frame_per_token()
    {
        var fast = new SlowChannel(delayMs: 0);
        await using var bus = new EventBus(fast, NullLogger.Instance, capacity: 1024);
        bus.Start();

        // 1000 个 token，一次性推入（远超 80ms 窗口）
        for (int i = 0; i < 1000; i++)
            bus.PublishDelta(EventKinds.TextDelta, "x");

        bus.FlushNow();
        await Task.Delay(500);

        var deltaFrames = fast.Frames.Count(f => f.Kind == EventKinds.TextDelta);
        // 合并后应该远少于 1000（通常 1~3 帧）
        Assert.True(deltaFrames < 50, $"合并后仍有 {deltaFrames} 帧，聚合未生效");

        // 内容不能丢
        var total = fast.Frames
            .Where(f => f.Kind == EventKinds.TextDelta)
            .Sum(f => f.Payload!.Value.GetProperty("text").GetString()!.Length);
        Assert.Equal(1000, total);
    }

    [Fact]
    public async Task Dropped_deltas_are_counted()
    {
        var slow = new SlowChannel(delayMs: 50);   // 极慢，必然队满
        await using var bus = new EventBus(slow, NullLogger.Instance, capacity: 8);
        bus.Start();

        for (int i = 0; i < 200; i++)
        {
            bus.PublishDelta(EventKinds.TextDelta, new string('y', 100));
            bus.FlushNow();      // 强制每帧独立
            await Task.Delay(1);
        }

        await Task.Delay(300);
        var dropped = bus.ConsumeDroppedCount();
        Assert.True(dropped > 0, "极慢消费者下应该有丢弃计数");
    }
#endif
}
