using System.Text.Json;
using System.Threading.Channels;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Core.Events;

/// <summary>出站帧：要么是事件，要么是应答。统一走一个 FIFO，保证顺序。</summary>
public abstract record OutgoingFrame;

public sealed record OutgoingEvent(EventFrame Frame) : OutgoingFrame;
public sealed record OutgoingResponse(ResponseFrame Frame) : OutgoingFrame;

/// <summary>
/// 事件总线：把"引擎产生的事件"可靠地送进通道。
///
/// ============ 三类帧的处理策略 ============
///
/// 【Delta 类】text.delta / reasoning.delta / tool.outputDelta
///   - 合并：80ms 窗口内累积成一个帧（帧率上限 12.5 帧/秒，远低于前端渲染能力）
///   - 可丢：队列满时丢弃最旧的 delta（并计数，后续 usage 帧带 dropped=true）
///   - 理由：丢掉一个 token 不影响理解，卡死界面才是灾难
///
/// 【State 类】turn.* / session.* / ask.* / state.* / tool.started/finished
///   - 不合并：每个都必须精确送达（顺序也重要）
///   - 不丢：队列满时 await 等待（把背压传导给 agent 循环）
///   - 理由：丢了会导致界面状态错乱（例如"工具卡片永远显示执行中"）
///
/// 【Notice/Usage 类】
///   - 不合并（但前端可以合并相同文本）
///   - 不丢
///
/// 【应答帧】经 PublishResponseAsync 进入同一 FIFO，保证应答与事件
///   按"产生顺序"写出，互不插队（文档 §2.3 修正：无"__res"占位）。
///
/// ============ 为什么允许阻塞 agent 循环 ============
/// "队列满"意味着"前端已经落后 1024 个状态帧"。此时让 agent 循环停下来
/// 等待是**正确**的行为：继续跑只会产生更多积压，而模型反正也要等前端
/// 才能看到结果。唯一风险是死锁——但如果前端卡死到这种程度，用户会
/// 中断或关窗，不需要我们自己"硬冲"。
/// </summary>
public sealed class EventBus : IAsyncDisposable
{
    private readonly Channel<OutgoingFrame> _queue;
    private readonly ILogger _log;
    private readonly IFrameChannel _channel;
    private readonly CancellationTokenSource _cts = new();

    private long _seq;
    private long _droppedDeltas;
    private bool _disposed;

    /// <summary>Delta 合并窗口（毫秒）。可用环境变量 HALO_DELTA_MS 覆盖。</summary>
    private readonly int _deltaWindowMs;

    // Delta 累积器（按 kind 分开，因为 text 与 reasoning 不能混在一起）
    private readonly object _deltaLock = new();
    private readonly Dictionary<string, System.Text.StringBuilder> _deltaBuffers = new();
    private readonly Dictionary<string, string> _deltaToolCallIds = new();
    private string _lastReasoningKind = "full";
    private DateTime _lastFlush = DateTime.UtcNow;

    private Task? _flushLoop;
    private Task? _writeLoop;

    public EventBus(IFrameChannel channel, ILogger logger, int capacity = 1024)
    {
        _channel = channel;
        _log = logger;

        var envMs = Environment.GetEnvironmentVariable("HALO_DELTA_MS");
        _deltaWindowMs = int.TryParse(envMs, out var v) && v > 0 ? v : 80;

        _queue = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,  // 我们自己控制"丢还是等"
        });
    }

    /// <summary>已发出的最后一个 seq（前端重同步用）。</summary>
    public long LastSeq => Interlocked.Read(ref _seq);

    public void Start()
    {
        _flushLoop = Task.Run(FlushLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    // ============================================================
    //  发布接口
    // ============================================================

    /// <summary>发布一个必须送达的状态事件。</summary>
    public async ValueTask PublishStateAsync(string kind, object? payload, CancellationToken ct)
    {
        var frame = MakeFrame(kind, payload);
        // 队满时等待（背压传导）
        await _queue.Writer.WriteAsync(new OutgoingEvent(frame), ct).ConfigureAwait(false);
    }

    /// <summary>发布一个应答帧（与事件走同一 FIFO，保证出站顺序）。</summary>
    public async ValueTask PublishResponseAsync(ResponseFrame frame, CancellationToken ct)
    {
        await _queue.Writer.WriteAsync(new OutgoingResponse(frame), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 发布一个可合并/可丢弃的 delta。
    /// **非阻塞**：立即返回，只写内存 buffer。
    /// </summary>
    public void PublishDelta(string kind, string text, string? toolCallId = null)
    {
        PublishDeltaWithKind(kind, text, null, toolCallId);
    }
    public void PublishReasoningDelta(string text, string? kind = null)
    {
        if (!string.IsNullOrEmpty(kind)) { lock (_deltaLock) _lastReasoningKind = kind; }
        PublishDeltaWithKind(EventKinds.ReasoningDelta, text, kind, null);
    }
    private void PublishDeltaWithKind(string kind, string text, string? reasoningKind, string? toolCallId)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_deltaLock)
        {
            if (!string.IsNullOrEmpty(reasoningKind)) _lastReasoningKind = reasoningKind;
            if (!_deltaBuffers.TryGetValue(kind, out var sb))
            {
                sb = new System.Text.StringBuilder(256);
                _deltaBuffers[kind] = sb;
            }
            sb.Append(text);
            if (toolCallId is not null) _deltaToolCallIds[kind] = toolCallId;
        }
    }

    /// <summary>强制立即 flush（回合结束时调用，保证最后一个 token 不丢）。</summary>
    public void FlushNow()
    {
        lock (_deltaLock) { _lastFlush = DateTime.MinValue; }
        TryFlushDeltas();
    }

    // ============================================================
    //  内部循环
    // ============================================================

    private async Task FlushLoopAsync()
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(10, _deltaWindowMs / 2));
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(period, _cts.Token).ConfigureAwait(false);
                TryFlushDeltas();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void TryFlushDeltas()
    {
        List<(string kind, string text, string? toolCallId)> batch = new(4);

        lock (_deltaLock)
        {
            var now = DateTime.UtcNow;
            if (_deltaBuffers.Count == 0) return;
            if (_lastFlush != DateTime.MinValue &&
                (now - _lastFlush).TotalMilliseconds < _deltaWindowMs) return;

            foreach (var kv in _deltaBuffers)
            {
                if (kv.Value.Length == 0) continue;
                _deltaToolCallIds.TryGetValue(kv.Key, out var tcId);
                batch.Add((kv.Key, kv.Value.ToString(), tcId));
                kv.Value.Clear();
            }
            _lastFlush = now;
        }

        foreach (var (kind, text, tcId) in batch)
        {
            object payload = kind switch
            {
                EventKinds.ReasoningDelta => new ReasoningDeltaPayload { Text = text, Kind = _lastReasoningKind },
                EventKinds.TextDelta => new TextDeltaPayload { Text = text },
                EventKinds.ToolOutputDelta => new ToolOutputDeltaPayload
                {
                    ToolCallId = tcId ?? "",
                    Delta = text,
                },
                _ => new TextDeltaPayload { Text = text },
            };

            var frame = MakeFrame(kind, payload);
            if (!_queue.Writer.TryWrite(new OutgoingEvent(frame)))
            {
                // 队满：丢 delta，计数
                Interlocked.Increment(ref _droppedDeltas);
                _log.LogDebug("delta 帧被丢弃（队满），kind={Kind} len={Len}", kind, text.Length);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    var frame = item switch
                    {
                        OutgoingEvent e => (Frame)e.Frame,
                        OutgoingResponse r => r.Frame,
                        _ => throw new InvalidOperationException("未知出站帧类型"),
                    };
                    await _channel.SendAsync(frame, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "写帧失败，通道可能已关闭");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _log.LogInformation("事件写出循环结束");
        }
    }

    private EventFrame MakeFrame(string kind, object? payload)
    {
        var seq = Interlocked.Increment(ref _seq) - 1;  // 从 0 开始
        return new EventFrame
        {
            Seq = seq,
            Kind = kind,
            Payload = payload is null
                ? null
                : JsonSerializer.SerializeToElement(payload, HaloJson.Options),
        };
    }

    /// <summary>取出并清零丢弃计数（usage 帧上报用）。</summary>
    public long ConsumeDroppedCount() => Interlocked.Exchange(ref _droppedDeltas, 0);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        FlushNow();

        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            if (_writeLoop is not null) await Task.WhenAny(_writeLoop, Task.Delay(2000)).ConfigureAwait(false);
            if (_flushLoop is not null) await Task.WhenAny(_flushLoop, Task.Delay(1000)).ConfigureAwait(false);
        }
        catch { }
        _cts.Dispose();
        _log.LogInformation("EventBus 已释放");
    }
}
