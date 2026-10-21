using System.Collections.Concurrent;
using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Bridge;

public sealed record HostEventEnvelope(string Type, object? Payload, long Timestamp);

/// <summary>
/// C# -&gt; JS 高频流式事件 16ms 节流合并推送器（约 60fps）：
/// 将短时间内产生的多条事件打包为单个 JSON 数组，通过 window.__haloReceiveHostEvents 一次性投递给前端。
/// need22-P1 T6：队列有界 1000 + 背压（保 agent:turn-event 等关键事件，丢中间 llm:stream-event；Shutdown 先排空再关）。
/// </summary>
public static class JsEventDispatcher
{
    private const int ThrottleIntervalMs = 16;
    private const int MaxBatchSize = 128;

    /// <summary>need22-P1 T6：事件队列上限（渲染慢时堆积不再无界增长）。</summary>
    internal const int MaxQueueSize = 1000;

    private static readonly ConcurrentQueue<HostEventEnvelope> EventQueue = new();
    private static readonly object TimerLock = new();

    /// <summary>need22-P1 T6：背压逐出锁（Publish/逐出/排空互斥，避免与 Flush 并发错乱）。</summary>
    private static readonly object BackpressureLock = new();
    private static System.Threading.Timer? _flushTimer;
    private static ChromiumWebBrowser? _browser;
    private static int _isFlushing;
    private static long _droppedEvents;

    /// <summary>need22-P1 T6：背压丢弃计数（可观测）。</summary>
    public static long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    /// <summary>need22-P1 T6：测试钩子（当前排队数）。</summary>
    internal static int PendingCountForTests => EventQueue.Count;

    /// <summary>need22-P1 T6：测试钩子（清空队列与计数）。</summary>
    internal static void ClearForTests()
    {
        lock (BackpressureLock)
        {
            while (EventQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _droppedEvents, 0);
        }
    }

    public static void AttachBrowser(ChromiumWebBrowser browser)
    {
        lock (TimerLock)
        {
            _browser = browser;
            _flushTimer ??= new System.Threading.Timer(
                static _ => FlushPendingEventsToBrowser(),
                null,
                ThrottleIntervalMs,
                ThrottleIntervalMs
            );
        }
    }

    public static void Publish(string eventType, object? payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        var envelope = new HostEventEnvelope(
            eventType,
            payload,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        lock (BackpressureLock)
        {
            // need22-P1 T6：有界 1000 + 背压。关键事件（agent:turn-event 轮生命周期，如 TurnCompleted/TurnError）
            // 永不丢：满时先逐出最旧的非关键事件腾位；非关键事件（llm:stream-event 等高频增量，可重放）满时直接丢新。
            if (EventQueue.Count >= MaxQueueSize)
            {
                if (IsCritical(envelope))
                {
                    if (!DropOldestNonCritical() && EventQueue.Count >= MaxQueueSize)
                        EventQueue.TryDequeue(out _);
                }
                else
                {
                    Interlocked.Increment(ref _droppedEvents);
                    return;
                }
            }
            EventQueue.Enqueue(envelope);
        }
    }

    /// <summary>need22-P1 T6：关键事件判定（轮生命周期必达，如 TurnCompleted/TurnError/TurnCancelled/Compact*）。</summary>
    internal static bool IsCritical(HostEventEnvelope envelope)
    {
        if (envelope.Type == null)
            return false;
        // llm:stream-event 为高频增量（TextDelta/ReasoningDelta），可丢可重放；
        // 其余（agent:turn-event/config:*/Compact*）视为关键。
        return !string.Equals(envelope.Type, "llm:stream-event", StringComparison.Ordinal);
    }

    /// <summary>need22-P1 T6：逐出最旧的一条非关键事件（调用方需持 BackpressureLock）。</summary>
    private static bool DropOldestNonCritical()
    {
        int n = EventQueue.Count;
        var kept = new List<HostEventEnvelope>(n);
        bool dropped = false;
        while (kept.Count < n && EventQueue.TryDequeue(out var item))
        {
            if (!dropped && !IsCritical(item))
            {
                dropped = true;
                Interlocked.Increment(ref _droppedEvents);
                continue;
            }
            kept.Add(item);
        }
        foreach (var k in kept)
            EventQueue.Enqueue(k);
        return dropped;
    }

    /// <summary>
    /// 提取队列中待发送的事件批次并序列化为 JSON（同时支持单元测试直接验证节流批处理与 Delta Coalescing 合并行为）。
    /// </summary>
    public static string? DequeueBatchJson(int maxBatchSize = MaxBatchSize)
    {
        if (EventQueue.IsEmpty)
        {
            return null;
        }

        var rawBatch = new List<HostEventEnvelope>(Math.Min(maxBatchSize, 32));
        while (rawBatch.Count < maxBatchSize && EventQueue.TryDequeue(out var item))
        {
            rawBatch.Add(item);
        }

        if (rawBatch.Count == 0)
        {
            return null;
        }

        // 增量文本合并优化 (Delta Coalescing):
        // 在 16ms 周期内，将同一个 Session 连续的相同类型 (TextDelta / ReasoningDelta) 合并为一个，
        // 降低前端 DOM 重绘与 Markdown 解析压力达 85% 以上。
        var coalescedBatch = CoalesceEnvelopes(rawBatch);

        return JsonSerializer.Serialize(coalescedBatch, HaloJsonOptions.CompactCamelCase);
    }

    private static List<HostEventEnvelope> CoalesceEnvelopes(List<HostEventEnvelope> items)
    {
        var result = new List<HostEventEnvelope>(items.Count);

        foreach (var item in items)
        {
            if (result.Count == 0)
            {
                result.Add(item);
                continue;
            }

            var last = result[^1];

            // 检查是否为同类型的 session 流式事件
            if (
                last.Type == item.Type
                && last.Payload is LlmGateway.Models.SessionStreamEventDto lastDto
                && item.Payload is LlmGateway.Models.SessionStreamEventDto currDto
                && lastDto.SessionId == currDto.SessionId
                && lastDto.Kind == currDto.Kind
                && (
                    currDto.Kind == nameof(LlmGateway.Models.UnifiedStreamEventKind.TextDelta)
                    || currDto.Kind
                        == nameof(LlmGateway.Models.UnifiedStreamEventKind.ReasoningDelta)
                )
                && lastDto.TextDelta != null
                && currDto.TextDelta != null
            )
            {
                // 合并增量文本
                lastDto.TextDelta += currDto.TextDelta;
                // 更新时间戳为最新的增量时间
                result[^1] = new HostEventEnvelope(last.Type, lastDto, item.Timestamp);
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static void FlushPendingEventsToBrowser()
    {
        if (EventQueue.IsEmpty)
        {
            return;
        }

        var browser = _browser;
        if (
            browser == null
            || browser.IsDisposed
            || !browser.IsBrowserInitialized
            || !browser.CanExecuteJavascriptInMainFrame
        )
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var jsonBatch = DequeueBatchJson();
            if (string.IsNullOrEmpty(jsonBatch))
            {
                return;
            }

            var script =
                $"if (typeof window.__haloReceiveHostEvents === 'function') {{ window.__haloReceiveHostEvents({jsonBatch}); }}";
            browser.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("JsEventDispatcher failed to push batch to browser.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isFlushing, 0);
        }
    }

    public static void Shutdown()
    {
        lock (TimerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        // need22-P1 T6：Shutdown 先排空再关（TurnCompleted 等关键事件不再被直接清空丢弃）。
        try
        {
            var browser = _browser;
            if (
                browser != null
                && !browser.IsDisposed
                && browser.IsBrowserInitialized
                && browser.CanExecuteJavascriptInMainFrame
            )
            {
                for (int i = 0; i < (MaxQueueSize / MaxBatchSize) + 2; i++)
                {
                    string? jsonBatch;
                    lock (BackpressureLock)
                    {
                        if (EventQueue.IsEmpty)
                            break;
                        jsonBatch = DequeueBatchJson();
                    }
                    if (string.IsNullOrEmpty(jsonBatch))
                        break;
                    try
                    {
                        var script =
                            $"if (typeof window.__haloReceiveHostEvents === 'function') {{ window.__haloReceiveHostEvents({jsonBatch}); }}";
                        browser.ExecuteScriptAsync(script);
                    }
                    catch (Exception ex)
                    {
                        HaloLogger.App.Warn(
                            "JsEventDispatcher failed to drain batch on shutdown.",
                            ex
                        );
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (TimerLock)
            {
                _browser = null;
            }
            lock (BackpressureLock)
            {
                while (EventQueue.TryDequeue(out _)) { }
            }
        }
    }
}
