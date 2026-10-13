using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.LlmGateway;

public interface ILlmGatewayClient
{
    IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
        UnifiedLlmRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// 统一 LLM 网关门面客户端：
/// 负责双协议路由（Chat / Resp）、请求序列化、代理与自定义 Header 注入、
/// 400 Bad Request 降级重试、审计追踪日志 (llm-trace-YYYYMMDD.log) 写入以及流式事件产出。
/// </summary>
public sealed class LlmGatewayClient : ILlmGatewayClient
{
    private readonly HaloHttpClientFactory _httpClientFactory;
    private readonly StoragePathManager _storageManager;
    private readonly Dictionary<LlmProtocolType, ILlmProtocolAdapter> _adapters;
    private readonly HttpClient? _customHttpClient;
    private static readonly ConcurrentDictionary<string, bool> RejectUsageFeatureProviders = new();
    private static readonly object LogFileLock = new();

    public LlmGatewayClient(
        StoragePathManager storageManager,
        HaloHttpClientFactory? httpClientFactory = null,
        IEnumerable<ILlmProtocolAdapter>? adapters = null,
        HttpClient? customHttpClient = null)
    {
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _httpClientFactory = httpClientFactory ?? new HaloHttpClientFactory();
        _customHttpClient = customHttpClient;

        _adapters = new Dictionary<LlmProtocolType, ILlmProtocolAdapter>();
        if (adapters != null)
        {
            foreach (var adapter in adapters)
            {
                _adapters[adapter.Protocol] = adapter;
            }
        }

        // 默认补充 ChatCompletions 与 ResponsesApi 适配器
        _adapters.TryAdd(LlmProtocolType.Chat, new ChatCompletionsAdapter());
        _adapters.TryAdd(LlmProtocolType.Resp, new ResponsesApiAdapter());
    }

    public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
        UnifiedLlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var channel = Channel.CreateUnbounded<UnifiedStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteStreamPipelineAsync(request, channel.Writer, ct).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }

    private async Task ExecuteStreamPipelineAsync(
        UnifiedLlmRequest request,
        ChannelWriter<UnifiedStreamEvent> writer,
        CancellationToken ct)
    {
        if (!_adapters.TryGetValue(request.Model.Protocol, out var adapter))
        {
            adapter = _adapters[LlmProtocolType.Chat];
        }

        string endpointUrl = adapter.ResolveEndpointUrl(request.Provider.BaseUrl);
        bool providerRejectsUsage = RejectUsageFeatureProviders.ContainsKey(request.Provider.Id);
        bool includeUsageInStream = !providerRejectsUsage;

        string requestPayload = adapter.SerializeRequest(request, includeUsageInStream);

        // 记录请求追踪日志
        LogRequestTrace(request, endpointUrl);

        var stopwatch = Stopwatch.StartNew();
        long? ttftMs = null;
        int inTokens = 0;
        int outTokens = 0;
        int cachedTokens = 0;
        int totalTokens = 0;
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        int toolCallCount = 0;
        int finalStatusCode = 200;

        HttpResponseMessage? response = null;
        HttpClient client = _customHttpClient ?? _httpClientFactory.CreateClient(request.Provider);

        try
        {
            response = await SendRequestAsync(client, request, endpointUrl, requestPayload, ct).ConfigureAwait(false);

            // 针对部分老旧接口不识别 stream_options 返回 400 Bad Request 的自动降级重试机制
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                includeUsageInStream &&
                request.Model.Protocol == LlmProtocolType.Chat)
            {
                HaloLogger.App.Warn($"[LlmGateway] Provider [{request.Provider.Name}] 返回 400 Bad Request，尝试降级移除 stream_options 重试");
                RejectUsageFeatureProviders[request.Provider.Id] = true;
                includeUsageInStream = false;
                requestPayload = adapter.SerializeRequest(request, includeUsageInStream: false);

                response.Dispose();
                response = await SendRequestAsync(client, request, endpointUrl, requestPayload, ct).ConfigureAwait(false);
            }

            finalStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = string.Empty;
                try
                {
                    errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略读取错误体的二次异常
                }

                string formattedError = adapter.FormatErrorResponse(finalStatusCode, errorBody);
                LogResponseTrace(request, finalStatusCode, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
                await writer.WriteAsync(UnifiedStreamEvent.Error($"HTTP_{finalStatusCode}", formattedError), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await foreach (var evt in adapter.ParseStreamAsync(responseStream, ct).ConfigureAwait(false))
            {
                if (!ttftMs.HasValue &&
                    (evt.Kind == UnifiedStreamEventKind.TextDelta ||
                     evt.Kind == UnifiedStreamEventKind.ReasoningDelta ||
                     evt.Kind == UnifiedStreamEventKind.ToolCallStarted))
                {
                    ttftMs = stopwatch.ElapsedMilliseconds;
                }

                if (evt.Kind == UnifiedStreamEventKind.ToolCallStarted && evt.ToolCall != null)
                {
                    toolCallCount++;
                    if (!string.IsNullOrEmpty(evt.ToolCall.FunctionName))
                    {
                        toolNames.Add(evt.ToolCall.FunctionName);
                    }
                }

                if (evt.Kind == UnifiedStreamEventKind.UsageUpdated && evt.Usage != null)
                {
                    inTokens = evt.Usage.InputTokens;
                    outTokens = evt.Usage.OutputTokens;
                    cachedTokens = evt.Usage.CachedInputTokens;
                    totalTokens = evt.Usage.TotalTokens;
                }

                await writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }

            LogResponseTrace(request, finalStatusCode, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogResponseTrace(request, 499, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
            await writer.WriteAsync(UnifiedStreamEvent.Cancelled(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"[LlmGateway] 调用异常 ({request.Provider.Name} / {request.Model.Id})", ex);
            LogResponseTrace(request, 500, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
            await writer.WriteAsync(UnifiedStreamEvent.Error("GATEWAY_EXCEPTION", ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(
        HttpClient client,
        UnifiedLlmRequest request,
        string endpointUrl,
        string requestPayload,
        CancellationToken ct)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
        {
            Content = new StringContent(requestPayload, Encoding.UTF8, "application/json")
        };

        // 1. 注入 API Key 认证头
        string rawApiKey = request.Provider.GetEffectiveApiKey();
        if (!string.IsNullOrWhiteSpace(rawApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawApiKey);
        }

        // 2. 注入预设变量自定义 Header（包含 ${session-hash} 等）
        HeaderVariableResolver.ApplyCustomHeaders(httpRequest, request.Provider, request.HeaderContext);

        return await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private void LogRequestTrace(UnifiedLlmRequest request, string endpointUrl)
    {
        try
        {
            string proxySummary = GetProxySummary(request.Provider);
            string headersSummary = GetCustomHeadersSummary(request.Provider, request.HeaderContext);

            string line = $"[REQ] reqId={request.RequestId} | session={request.HeaderContext.SessionId} (hash={request.HeaderContext.SessionHash}) | provider={request.Provider.Name} | model={request.Model.Id} | protocol={request.Model.Protocol.ToString().ToUpperInvariant()} | url={endpointUrl} | proxy={proxySummary} | customHeaders=[{headersSummary}]";
            AppendAuditTrace(line);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("写入 LLM Trace 请求审计失败", ex);
        }
    }

    private void LogResponseTrace(
        UnifiedLlmRequest request,
        int statusCode,
        long totalMs,
        long? ttftMs,
        int inTokens,
        int outTokens,
        int cachedTokens,
        int totalTokens,
        int toolCallCount,
        HashSet<string> toolNames)
    {
        try
        {
            double hitRate = (inTokens > 0) ? ((double)cachedTokens / inTokens * 100.0) : 0.0;
            string toolCallsText = toolCallCount > 0
                ? $"{toolCallCount}({string.Join(",", toolNames)})"
                : "0";

            long effectiveTtft = ttftMs ?? totalMs;
            string line = $"[RES] reqId={request.RequestId} | status={statusCode} | ttftMs={effectiveTtft} | totalMs={totalMs} | inTokens={inTokens} (cached={cachedTokens}, hitRate={hitRate:F1}%) | outTokens={outTokens} | toolCalls={toolCallsText}";
            AppendAuditTrace(line);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("写入 LLM Trace 响应审计失败", ex);
        }
    }

    private void AppendAuditTrace(string line)
    {
        string logsDir = _storageManager.LogsDirectory;
        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        string logFile = Path.Combine(logsDir, $"llm-trace-{DateTime.UtcNow:yyyyMMdd}.log");
        string fullLine = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] {line}{Environment.NewLine}";

        lock (LogFileLock)
        {
            File.AppendAllText(logFile, fullLine, Encoding.UTF8);
        }
    }

    private static string GetProxySummary(ProviderConfig provider)
    {
        return provider.ProxyMode switch
        {
            ProviderProxyMode.None => "DIRECT",
            ProviderProxyMode.Custom when provider.CustomProxy != null =>
                $"CUSTOM({provider.CustomProxy.Scheme}://{provider.CustomProxy.Host}:{provider.CustomProxy.Port})",
            _ => "SYSTEM(Default)"
        };
    }

    private static string GetCustomHeadersSummary(ProviderConfig provider, HeaderResolutionContext context)
    {
        if (!provider.EnableCustomHeaders || provider.CustomHeaders == null || provider.CustomHeaders.Count == 0)
        {
            return string.Empty;
        }

        var list = new List<string>();
        foreach (var h in provider.CustomHeaders)
        {
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Key))
            {
                string resolved = HeaderVariableResolver.ResolveTemplate(h.ValueTemplate ?? string.Empty, context);
                list.Add($"{h.Key.Trim()}={resolved}");
            }
        }

        return string.Join(", ", list);
    }
}
