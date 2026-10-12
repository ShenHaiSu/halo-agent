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
/// </summary>
public static class JsEventDispatcher
{
    private const int ThrottleIntervalMs = 16;
    private const int MaxBatchSize = 128;

    private static readonly ConcurrentQueue<HostEventEnvelope> EventQueue = new();
    private static readonly object TimerLock = new();
    private static System.Threading.Timer? _flushTimer;
    private static ChromiumWebBrowser? _browser;
    private static int _isFlushing;

    public static void AttachBrowser(ChromiumWebBrowser browser)
    {
        lock (TimerLock)
        {
            _browser = browser;
            _flushTimer ??= new System.Threading.Timer(
                static _ => FlushPendingEventsToBrowser(),
                null,
                ThrottleIntervalMs,
                ThrottleIntervalMs);
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
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        EventQueue.Enqueue(envelope);
    }

    /// <summary>
    /// 提取队列中待发送的事件批次并序列化为 JSON（同时支持单元测试直接验证节流批处理行为）。
    /// </summary>
    public static string? DequeueBatchJson(int maxBatchSize = MaxBatchSize)
    {
        if (EventQueue.IsEmpty)
        {
            return null;
        }

        var batch = new List<HostEventEnvelope>(Math.Min(maxBatchSize, 32));
        while (batch.Count < maxBatchSize && EventQueue.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(batch, HaloJsonOptions.CompactCamelCase);
    }

    private static void FlushPendingEventsToBrowser()
    {
        if (EventQueue.IsEmpty)
        {
            return;
        }

        var browser = _browser;
        if (browser == null || browser.IsDisposed || !browser.IsBrowserInitialized || !browser.CanExecuteJavascriptInMainFrame)
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

            var script = $"if (typeof window.__haloReceiveHostEvents === 'function') {{ window.__haloReceiveHostEvents({jsonBatch}); }}";
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
            _browser = null;
        }

        while (EventQueue.TryDequeue(out _))
        {
        }
    }
}
