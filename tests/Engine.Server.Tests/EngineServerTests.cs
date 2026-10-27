using System.Collections.Concurrent;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Channels;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Server.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Server.Tests;

/// <summary>
/// 引擎端到端测试：InProc 通道 + 真实 FrameServer，验证
/// 并发配对（A-5）、超时不泄漏（A-6）、事件全局有序（A-11）。
///
/// 组成（与 06-验收方法 §2.5/§2.6/§2.10 对应）：
///   InProcChannel 对（内存零拷贝）+ TestClient（简易请求封装，
///   id → TCS 配对、超时清理）+ MethodRouter + StubMethods +
///   EventBus + FrameServer 真实跑。
/// </summary>
public class EngineServerTests
{
    /// <summary>
    /// 简易测试客户端：给通道包一层请求-应答配对 + 事件收集。
    /// 生产对应物是宿主侧的 EngineClient（D1-4），这里只保留测试够用的最小语义。
    /// </summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly IFrameChannel _ch;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<ResponseFrame>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readLoop;
        private long _nextId;
        private bool _disposed;

        /// <summary>收到的事件 seq（A-11 有序性断言用）。</summary>
        public readonly ConcurrentQueue<long> SeenEventSeqs = new();

        public TestClient(IFrameChannel ch)
        {
            _ch = ch;
            _readLoop = Task.Run(ReadLoopAsync);
        }

        public async Task<T?> RequestAsync<T>(string method, object? prms,
            int timeoutMs = 10000, CancellationToken ct = default)
        {
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<ResponseFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                JsonElement? p = prms is null
                    ? null
                    : JsonSerializer.SerializeToElement(prms, HaloJson.Options);
                await _ch.SendAsync(new RequestFrame { Id = id, Method = method, Params = p }, ct)
                    .ConfigureAwait(false);

                ResponseFrame res;
                try
                {
                    res = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 超时：清理挂起项（不泄漏），连接保持可用
                    _pending.TryRemove(id, out _);
                    throw new TimeoutException($"请求 {method} 超时（{timeoutMs}ms）");
                }

                if (!res.Ok)
                {
                    var err = res.Error;
                    throw new RpcException(
                        err?.Code ?? RpcError.Internal,
                        err?.Msg ?? "未知错误",
                        err?.Hint);
                }

                return res.Result is null
                    ? default
                    : JsonSerializer.Deserialize<T>(res.Result.Value.GetRawText(), HaloJson.Options);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    ReceiveResult r;
                    try
                    {
                        r = await _ch.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (r.IsClosed) break;
                    if (r.IsMalformed) continue;

                    switch (r.Frame)
                    {
                        case ResponseFrame res:
                            if (_pending.TryRemove(res.Id, out var p))
                                p.TrySetResult(res);
                            break;
                        case EventFrame ev:
                            SeenEventSeqs.Enqueue(ev.Seq);
                            break;
                    }
                }
            }
            finally
            {
                foreach (var kv in _pending)
                {
                    if (_pending.TryRemove(kv.Key, out var p))
                        p.TrySetException(new InvalidOperationException("连接已关闭"));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }
            _cts.Dispose();
        }
    }

    // ============================================================
    //  以下 3 个用例需要后端 D1-3/D1-4 的新 FrameServer 管线：
    //    MethodRouter + StubMethods + EngineState
    //      （src/Engine.Server/Routing/*, src/Engine.Server/EngineState.cs）
    //    EventBus（src/Engine.Core/Events/EventBus.cs）
    //    新 FrameServer 构造（channel, state, bus, logger, ct）
    //    方法 DTO（HaloAgent.Engine.Abstractions.Methods，如 SystemPingResult）
    //  上述类型落地后：定义编译符号 P1_BACKEND_ROUTING
    // （或直接删掉本文件的 #if / #endif），即可启用。
    // ============================================================

    [Fact]
    public async Task Concurrent_100_requests_correctly_paired()
    {
        var (client, server) = InProcChannel.CreatePair();
        // 启动 FrameServer
        var router = new MethodRouter();
        var state = new EngineState(Path.GetTempPath(), router);
        var stub = new StubMethods(state);
        router.RegisterAll(
            ("system.ping", stub.SystemPing),
            ("state.snapshot", stub.StateSnapshot));

        await using var s = server;
        var cts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            await using var bus = new EventBus(s, NullLogger.Instance);
            var fs = new FrameServer(s, state, bus, NullLogger.Instance, cts.Token);
            await fs.RunAsync();
        });

        await using var c = client;
        await using var client2 = new TestClient(c);

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0)
                return (object?)await client2.RequestAsync<SystemPingResult>("system.ping", null);
            return await client2.RequestAsync<StateSnapshot>("state.snapshot", null);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // 偶数是 ping，奇数是 snapshot
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0) Assert.IsType<SystemPingResult>(results[i]);
            else Assert.IsType<StateSnapshot>(results[i]);
        }

        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(3000));
    }

    [Fact]
    public async Task Timed_out_request_does_not_leak_and_connection_survives()
    {
        var (client, server) = InProcChannel.CreatePair();
        var router = new MethodRouter();
        var state = new EngineState(Path.GetTempPath(), router);
        var stub = new StubMethods(state);
        router.RegisterAll(
            ("system.ping", stub.SystemPing),
            ("state.snapshot", stub.StateSnapshot));
        // 用一个永不返回的 handler
        router.Register("slow.never", (_, __) => new TaskCompletionSource<object?>().Task);

        await using var s = server;
        var cts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            await using var bus = new EventBus(s, NullLogger.Instance);
            var fs = new FrameServer(s, state, bus, NullLogger.Instance, cts.Token);
            await fs.RunAsync();
        });

        await using var c = client;
        await using var tc = new TestClient(c);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            tc.RequestAsync<object>("slow.never", null, timeoutMs: 500));

        // 后续请求仍可用
        var pong = await tc.RequestAsync<SystemPingResult>("system.ping", null);
        Assert.True(pong!.Pong);

        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(3000));
    }

    [Fact]
    public async Task Mixed_events_and_responses_preserve_global_order()
    {
        // 一边并发发 500 个请求，一边推 500 个事件；
        // 收集所有收到的 EventFrame.Seq，断言严格递增
        // （出站统一走 EventBus 单 FIFO，见 04-实现方法-下 §2.3）。
        var (client, server) = InProcChannel.CreatePair();
        var router = new MethodRouter();
        var state = new EngineState(Path.GetTempPath(), router);
        var stub = new StubMethods(state);
        router.RegisterAll(
            ("system.ping", stub.SystemPing),
            ("state.snapshot", stub.StateSnapshot));

        await using var s = server;
        var cts = new CancellationTokenSource();
        EventBus? busRef = null;
        var serverTask = Task.Run(async () =>
        {
            await using var bus = new EventBus(s, NullLogger.Instance);
            busRef = bus;
            var fs = new FrameServer(s, state, bus, NullLogger.Instance, cts.Token);
            await fs.RunAsync();
        });

        await using var c = client;
        await using var tc = new TestClient(c);

        var reqTasks = Enumerable.Range(0, 500).Select(_ => Task.Run(async () =>
        {
            await tc.RequestAsync<SystemPingResult>("system.ping", null);
        })).ToArray();

        var evtTask = Task.Run(async () =>
        {
            while (busRef is null) await Task.Delay(10);
            for (int i = 0; i < 500; i++)
                await busRef.PublishStateAsync(
                    "notice", new { text = $"n{i}", level = "info" }, cts.Token);
        });

        await Task.WhenAll(reqTasks);
        await evtTask;

        // 等事件全部到达
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (tc.SeenEventSeqs.Count < 500 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(500, tc.SeenEventSeqs.Count);

        var seqs = tc.SeenEventSeqs.ToArray();
        for (int i = 1; i < seqs.Length; i++)
            Assert.True(seqs[i] > seqs[i - 1], $"seq 乱序: {seqs[i - 1]} → {seqs[i]}");

        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(3000));
    }
}
