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

         // need22-P1 T2：Task.Run 透传 ct（取消后不再调度旧轮管线；已取消则直接不启动，读端随即 OCE 退出）。
         _ = Task.Run(async () =>
         {
             try
             {
                 await ExecuteStreamPipelineAsync(request, channel.Writer, ct).ConfigureAwait(false);
             }
             finally
             {
                 // need22-P1 T2：取消后直接 TryComplete，不再写旧事件串扰新轮。
                 channel.Writer.TryComplete();
             }
         }, ct);

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
 
         // need05-06：本地预测（分块 LCP 推测 + 拼接对比），只记日志/预测存储，不参与实际命中率口径。
         double? predictedHit = null; int predictedCached = 0; string divergenceBlock = "-";
         try
         {
             var snapshot = Services.PrefixCachePredictor.BuildSnapshot(request.SystemInstructions, request.Messages, request.Tools);
             (predictedHit, predictedCached, divergenceBlock) = Services.PrefixCachePredictor.TrackRequest(
                 request.HeaderContext.SessionHash, request.HeaderContext.SessionId, snapshot);
         }
         catch { /* 预测永不阻塞主链路 */ }
 
          // need05-02：前缀哈希（整包确定性哈希；稳定即 prompt cache 命中前提）
          string prefixHash = HeaderVariableResolver.ComputeDeterministicHash16(requestPayload);
           try
           {
               // need09-02：sysHash → stableHash（stable 区）+ fullHash（整包 system 文本）双记；
               // PrefixHash 恒等于 fullHash，老 Bridge 对比行为不变。
               string fullSys = request.SystemInstructions ?? string.Empty;
               var (stablePart, _) = Services.PrefixCachePredictor.SplitSystem(fullSys);
               string stableHash = HeaderVariableResolver.ComputeDeterministicHash16(stablePart ?? string.Empty);
               string fullHash = HeaderVariableResolver.ComputeDeterministicHash16(fullSys);
               Services.PromptLastSentStore.Record(request.HeaderContext.SessionId, new Services.LastSentPromptSnapshot { ReqId = request.RequestId, SessionId = request.HeaderContext.SessionId, WorkspaceId = request.HeaderContext.WorkspaceId, PrefixHash = fullHash, StableHash = stableHash, FullHash = fullHash, CharLength = fullSys.Length, SentAtUtc = DateTime.UtcNow.ToString("o") });
           }
           catch { }
          // 记录请求追踪日志
          LogRequestTrace(request, endpointUrl, prefixHash, predictedHit, predictedCached, divergenceBlock);

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

            // need17-01 Prefer：payload 含 strict 且网关 400 明确提及 strict/unknown keyword 时，
            // 去 strict 重发一次并 Warn（仅一次，不循环；Require/Off 由配置层控制是否发送）。
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                requestPayload.Contains("\"strict\""))
            {
                string peekBody = await PeekErrorBodyAsync(response, ct).ConfigureAwait(false);
                if (IsStrictRejection(peekBody))
                {
                    HaloLogger.App.Warn($"[LlmGateway] Provider [{request.Provider.Name}] 不支持 strict 工具声明（400 提及 strict），已去 strict 重发一次");
                    requestPayload = StripStrictFromPayload(requestPayload);
                    response.Dispose();
                    response = await SendRequestAsync(client, request, endpointUrl, requestPayload, ct).ConfigureAwait(false);
                }
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
                 // need22-P1 T2：取消后不再写旧事件（错误码 HTTP_{code} 照常透传给未取消的调用方）。
                 if (!ct.IsCancellationRequested)
                     await writer.WriteAsync(UnifiedStreamEvent.Error($"HTTP_{finalStatusCode}", formattedError), ct).ConfigureAwait(false);
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

                 if (!ct.IsCancellationRequested)
                     await writer.WriteAsync(evt, ct).ConfigureAwait(false);
             }

             RecordPredictionActual(request, inTokens, cachedTokens);
             LogResponseTrace(request, finalStatusCode, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames, predictedHit, predictedCached, divergenceBlock);
         }
         catch (OperationCanceledException) when (ct.IsCancellationRequested)
         {
             LogResponseTrace(request, 499, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
             // need22-P1 T2：读端已随 ct 退出，此处不再补写 Cancelled（TryComplete 即 enough）。
         }
         catch (Exception ex)
         {
             HaloLogger.App.Error($"[LlmGateway] 调用异常 ({request.Provider.Name} / {request.Model.Id})", ex);
             LogResponseTrace(request, 500, stopwatch.ElapsedMilliseconds, ttftMs, inTokens, outTokens, cachedTokens, totalTokens, toolCallCount, toolNames);
             // need22-P1 T3：网关侧超时（HttpClient.Timeout/传输超时且非用户取消）透传 TIMEOUT 码，其它异常沿用 GATEWAY_EXCEPTION。
             string code = IsTimeoutException(ex) ? "TIMEOUT" : "GATEWAY_EXCEPTION";
             if (!ct.IsCancellationRequested)
                 await writer.WriteAsync(UnifiedStreamEvent.Error(code, ex.Message), ct).ConfigureAwait(false);
         }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// need17-01 Prefer：判断 400 错误体是否为 strict 关键字拒绝（单测可直接覆盖）。
    /// 命中条件：提及 strict（unknown keyword/unsupported parameter 等第三方网关文案）。
    /// </summary>
    internal static bool IsStrictRejection(string? errorBody)
    {
        if (string.IsNullOrEmpty(errorBody))
        {
            return false;
        }
        return errorBody!.IndexOf("strict", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// need17-01 Prefer：从请求 payload 中剥离全部 "strict":true|false 声明（单测可直接覆盖）。
    /// 兼容行内/行尾两种逗号位置；剥离后做一次 JSON 结构兜底（失败则原样返回，避免发坏包）。
    /// </summary>
    internal static string StripStrictFromPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload) || !payload.Contains("\"strict\""))
        {
            return payload;
        }
        try
        {
            string s = System.Text.RegularExpressions.Regex.Replace(
                payload, @",\s*""strict""\s*:\s*(true|false)", string.Empty);
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"""strict""\s*:\s*(true|false)\s*,", string.Empty);
            s = s.Replace("{,", "{").Replace("[,", "[");
            // 兜底：剥离后必须仍是合法 JSON，否则返回原 payload。
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return s;
        }
        catch
        {
            return payload;
        }
    }

    private static async Task<string> PeekErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 2048 ? body[..2048] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

     /// <summary>need22-P1 T3：重试退避等待（测试可替换为立即返回以加速单测）。</summary>
     internal static Func<int, CancellationToken, Task> RetryDelayAsync = (ms, ct) => Task.Delay(ms, ct);

     /// <summary>need22-P1 T3：网关超时判定（HttpClient.Timeout 抛 TaskCanceledException；ct 未取消时即超时）。</summary>
     internal static bool IsTimeoutException(Exception? ex)
     {
         if (ex == null) return false;
         if (ex is TimeoutException) return true;
         if (ex is TaskCanceledException) return true;
         if (ex is IOException io && io.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return true;
         return false;
     }

     private static bool IsTransientTransportError(Exception ex, CancellationToken ct)
     {
         if (ct.IsCancellationRequested) return false;
         return ex is TaskCanceledException || ex is TimeoutException
             || ex is HttpRequestException || ex is IOException;
     }

     /// <summary>
     /// need22-P1 T3：429/5xx/传输超时指数退避重试（初始 1 次 + 最多 3 次重试，退避 1s/2s/4s+抖动）。
     /// 400 等其它状态码直接返回，由调用方走降级/透传逻辑；错误码 HTTP_{code} 保持透传。
     /// </summary>
     private static async Task<HttpResponseMessage> SendRequestAsync(
         HttpClient client,
         UnifiedLlmRequest request,
         string endpointUrl,
         string requestPayload,
         CancellationToken ct)
     {
         HttpResponseMessage? retryableResponse = null;
         try
         {
             for (int attempt = 0; ; attempt++)
             {
                 if (attempt > 0)
                 {
                     int baseMs = attempt == 1 ? 1000 : attempt == 2 ? 2000 : 4000;
                     int delayMs = baseMs + Random.Shared.Next(0, 500);
                     await RetryDelayAsync(delayMs, ct).ConfigureAwait(false);
                 }
                 retryableResponse?.Dispose();
                 retryableResponse = null;
                 HttpResponseMessage response;
                 try
                 {
                     response = await SendOnceAsync(client, request, endpointUrl, requestPayload, ct).ConfigureAwait(false);
                 }
                 catch (Exception ex) when (IsTransientTransportError(ex, ct))
                 {
                     HaloLogger.App.Warn($"[LlmGateway] 传输异常可重试（第{attempt + 1}/4 次）：{ex.GetType().Name} {ex.Message}");
                     if (attempt >= 3) throw;
                     continue;
                 }
                 int code = (int)response.StatusCode;
                 bool retryableStatus = code == 429 || (code >= 500 && code <= 599);
                 if (retryableStatus && attempt < 3)
                 {
                     HaloLogger.App.Warn($"[LlmGateway] HTTP {code} 可重试（第{attempt + 1}/4 次），退避后重试");
                     retryableResponse = response;
                     continue;
                 }
                 return response;
             }
         }
         catch
         {
             retryableResponse?.Dispose();
             throw;
         }
     }

     private static async Task<HttpResponseMessage> SendOnceAsync(
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

     private void LogRequestTrace(UnifiedLlmRequest request, string endpointUrl, string? prefixHash = null, double? predictedHit = null, int predictedCached = 0, string? divergenceBlock = null)
     {
         try
         {
             string proxySummary = GetProxySummary(request.Provider);
             string headersSummary = GetCustomHeadersSummary(request.Provider, request.HeaderContext);
             // need05-02：[REQ] 追加 prefixHash + msgCount + inputEst（联调 grep reqId 看哈希变化 vs 命中率因果）
             // need05-06：追加 predictedHit/divergence（发送前本地推测，不代表账单口径）
             // need09-02：追加 skillL0/skillTokens/memPinned/memTokens/imgCount/imgTruncated（只读解析 stable 文本与消息引用）
             int msgCount = request.Messages?.Count ?? 0;
             int inputEst = 0;
             int imgCount = 0;
             bool imgTruncated = false;
             if (request.Messages != null)
             {
                 foreach (var m in request.Messages)
                 {
                     inputEst += Services.TokenBudgetService.EstimateMessageTokens(m);
                     var (live, omitted) = Services.TokenBudgetService.CountMessageImages(m);
                     imgCount += live + omitted;
                     if (omitted > 0) imgTruncated = true;
                 }
             }
             int skillL0 = 0, skillTokens = 0, memPinned = 0, memTokens = 0;
             try
             {
                 var (stableText, _) = Services.PrefixCachePredictor.SplitSystem(request.SystemInstructions);
                 var (_, skillPart, memPart) = Services.PrefixCachePredictor.SplitStableSystem(stableText);
                 skillL0 = CountStableIndexRows(skillPart);
                 skillTokens = Services.TokenBudgetService.EstimateTokens(skillPart);
                 memPinned = CountStableMemorySections(memPart);
                 memTokens = Services.TokenBudgetService.EstimateTokens(memPart);
             }
             catch { /* 统计永不阻塞主链路 */ }

             string predText = predictedHit.HasValue ? $"{predictedHit.Value * 100.0:F1}%" : "-";
             string line = $"[REQ] reqId={request.RequestId} | session={request.HeaderContext.SessionId} (hash={request.HeaderContext.SessionHash}) | provider={request.Provider.Name} | model={request.Model.Id} | protocol={request.Model.Protocol.ToString().ToUpperInvariant()} | url={endpointUrl} | proxy={proxySummary} | customHeaders=[{headersSummary}] | prefixHash={prefixHash ?? "-"} | msgCount={msgCount} | inputEst={inputEst} | predictedHit={predText} (cached~{predictedCached}, div={divergenceBlock ?? "-"}) | skillL0={skillL0} skillTokens={skillTokens} memPinned={memPinned} memTokens={memTokens} imgCount={imgCount} imgTruncated={imgTruncated}";
             AppendAuditTrace(line);
         }
         catch (Exception ex)
         {
             HaloLogger.App.Warn("写入 LLM Trace 请求审计失败", ex);
         }
     }
 
     /// <summary>need09-02：统计 skill 索引行数（行首 `- [` 的行）。</summary>
     private static int CountStableIndexRows(string? skillPart)
     {
         if (string.IsNullOrEmpty(skillPart)) return 0;
         int n = 0;
         foreach (var line in skillPart.Split('\n'))
             if (line.TrimStart().StartsWith("- [", StringComparison.Ordinal)) n++;
         return n;
     }

     /// <summary>need09-02：统计 pinned memory 节数（行首 `### ` 的行）。</summary>
     private static int CountStableMemorySections(string? memPart)
     {
         if (string.IsNullOrEmpty(memPart)) return 0;
         int n = 0;
         foreach (var line in memPart.Split('\n'))
             if (line.TrimStart().StartsWith("### ", StringComparison.Ordinal)) n++;
         return n;
     }
 
     private static void RecordPredictionActual(UnifiedLlmRequest request, int inTokens, int cachedTokens)
     {
         try
         {
             if (inTokens > 0)
                 Services.PrefixCachePredictor.RecordActual(request.HeaderContext.SessionHash, request.HeaderContext.SessionId, inTokens, cachedTokens);
         }
         catch { /* 预测存储永不抛 */ }
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
         HashSet<string> toolNames,
         double? predictedHit = null,
         int predictedCached = 0,
         string? divergenceBlock = null)
     {
         try
         {
             double hitRate = (inTokens > 0) ? ((double)cachedTokens / inTokens * 100.0) : 0.0;
             string toolCallsText = toolCallCount > 0
                 ? $"{toolCallCount}({string.Join(",", toolNames)})"
                 : "0";
 
             long effectiveTtft = ttftMs ?? totalMs;
             string predText = predictedHit.HasValue ? $"{predictedHit.Value * 100.0:F1}%" : "-";
             string line = $"[RES] reqId={request.RequestId} | status={statusCode} | ttftMs={effectiveTtft} | totalMs={totalMs} | inTokens={inTokens} (cached={cachedTokens}, hitRate={hitRate:F1}%) | outTokens={outTokens} | toolCalls={toolCallsText} | predictedHit={predText} (cached~{predictedCached}, div={divergenceBlock ?? "-"})";
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
