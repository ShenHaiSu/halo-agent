using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Server.Routing;
using Microsoft.Extensions.Logging;

// IFrameChannel 三态（文档 §4.3）由 Engine.Abstractions.Frames 提供；
// 通道实现（NamedPipe/Stdio/InProc）在 Engine.Core.Channels（类库），引擎与宿主共用。

namespace HaloAgent.Engine.Server;

/// <summary>
/// 帧服务主循环。
///
/// ============ 并发模型（P1 定稿） ============
///
/// 【读】单线程顺序读帧 → 保证请求到达顺序可预期。
///
/// 【写】由 EventBus 独立线程负责 → 请求处理与事件推送互不阻塞。
///   所有出站帧（应答 + 事件）走 EventBus 的同一 FIFO，
///   保证"产生顺序"写出，应答与事件不会互相插队错乱（文档 §2.3 修正）。
///
/// 【处理】**并发**：每个请求投进 worker 队列，由 N 个 worker 消费。
///   为什么？慢请求（如 session.compact 要调模型）不能阻塞后续请求的读取。
///   但要注意：
///     - 同一连接上，**写状态的操作必须串行**（否则状态竞争）
///     - 读操作（ping/info/snapshot）可以并发
///   实现：用 SemaphoreSlim(1) 保护"写状态类"方法。
/// </summary>
public sealed class FrameServer
{
    private readonly IFrameChannel _channel;
    private readonly ILogger _log;
    private readonly EngineState _state;
    private readonly EventBus _bus;
    private readonly System.Threading.Channels.Channel<ReqItem> _work;
    private readonly SemaphoreSlim _mutateLock = new(1, 1);
    private readonly CancellationTokenSource _cts;

    private sealed record ReqItem(long Id, string Method, JsonElement? Params);

    /// <summary>需要串行执行的方法（会改状态）。其余方法可并发。</summary>
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
    {
        "agent.prompt", "agent.queue", "agent.unqueue", "agent.cancel", "agent.answer",
        "agent.drain", "session.new", "session.resume", "session.rename", "session.delete",
        "session.clear", "session.compact", "session.setCwd",
        "state.setMode", "state.setPersona",
        "state.setMaxTurns", "state.setRetry",
        "settings.saveProviders", "settings.setActive", "settings.saveNetwork",
        "settings.mcp.save",

        "asset.skills.save", "asset.skills.delete", "asset.skills.run",
        "asset.personas.save", "asset.memory.save", "asset.memory.delete",
        "tool.undoRollback", "system.shutdown", "update.install",
    };

    public FrameServer(IFrameChannel channel, EngineState state, EventBus bus,
        ILogger logger, CancellationToken ct)
    {
        _channel = channel;
        _state = state;
        _bus = bus;
        _log = logger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _work = System.Threading.Channels.Channel.CreateBounded<ReqItem>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });
    }

    public async Task RunAsync()
    {
        _bus.Start();
        var workers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount))
            .Select(_ => Task.Run(WorkerLoopAsync))
            .ToArray();

        try
        {
            while (!_cts.IsCancellationRequested && !_state.ShutdownRequested)
            {
                ReceiveResult r;
                try
                {
                    r = await _channel.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (r.IsClosed)
                {
                    _log.LogInformation("对端关闭，停止读循环");
                    break;
                }
                if (r.IsMalformed)
                {
                    _log.LogWarning("坏帧已忽略: {Error}", r.Error);
                    continue;    // 坏帧不中断连接
                }

                switch (r.Frame)
                {
                    case RequestFrame req:
                        await _work.Writer.WriteAsync(
                            new ReqItem(req.Id, req.Method, req.Params), _cts.Token)
                            .ConfigureAwait(false);
                        break;

                    case EventFrame:
                        _log.LogWarning("引擎收到事件帧，忽略");
                        break;

                    case ResponseFrame:
                        _log.LogWarning("引擎收到应答帧，忽略");
                        break;
                }
            }
        }
        finally
        {
            _work.Writer.TryComplete();
            try { await Task.WhenAll(workers); } catch { }
            await _bus.DisposeAsync();
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await HandleAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(ReqItem item)
    {
        _log.LogDebug("<- {Method} id={Id}", item.Method, item.Id);

        bool needsLock = MutatingMethods.Contains(item.Method);
        if (needsLock) await _mutateLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (!_state.Router.TryGet(item.Method, out var handler))
                throw RpcException.MethodNotFound(item.Method);

            var result = await handler(item.Params, _cts.Token).ConfigureAwait(false);
            var res = ResponseFrame.Success(item.Id, result, HaloJson.Options);
            await _bus.PublishResponseAsync(res, _cts.Token).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            await SendResponseAsync(ResponseFrame.Failure(item.Id, ex.Code, ex.Message), ex.Hint)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendResponseAsync(
                ResponseFrame.Failure(item.Id, RpcError.Internal, "请求被取消"), null)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "处理 {Method} 时异常", item.Method);
            await SendResponseAsync(
                ResponseFrame.Failure(item.Id, RpcError.Internal, ex.Message), null)
                .ConfigureAwait(false);
        }
        finally
        {
            if (needsLock) _mutateLock.Release();
        }
    }

    /// <summary>应答经 EventBus 发出（与事件同一 FIFO，不断通道的发送锁语义）。</summary>
    private Task SendResponseAsync(ResponseFrame frame, string? hint)
    {
        if (hint is not null)
        {
            frame = frame with
            {
                Error = frame.Error is null ? null : frame.Error with { Hint = hint },
            };
        }
        return _bus.PublishResponseAsync(frame, _cts.Token).AsTask();
    }
}
