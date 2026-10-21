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

/// <summary>
/// 统一 LLM 网关门面客户端：
/// 负责双协议路由（Chat / Resp）、请求序列化、代理与自定义 Header 注入、
/// 400 Bad Request 降级重试、审计追踪日志 (llm-trace-YYYYMMDD.log) 写入以及流式事件产出。
/// </summary>
public sealed partial class LlmGatewayClient : ILlmGatewayClient
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
        HttpClient? customHttpClient = null
    )
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
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var channel = Channel.CreateUnbounded<UnifiedStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
        );

        // need22-P1 T2：Task.Run 透传 ct（取消后不再调度旧轮管线；已取消则直接不启动，读端随即 OCE 退出）。
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ExecuteStreamPipelineAsync(request, channel.Writer, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // need22-P1 T2：取消后直接 TryComplete，不再写旧事件串扰新轮。
                    channel.Writer.TryComplete();
                }
            },
            ct
        );

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
        CancellationToken ct
    )
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
        double? predictedHit = null;
        int predictedCached = 0;
        string divergenceBlock = "-";
        try
        {
            var snapshot = Services.PrefixCachePredictor.BuildSnapshot(
                request.SystemInstructions,
                request.Messages,
                request.Tools
            );
            (predictedHit, predictedCached, divergenceBlock) =
                Services.PrefixCachePredictor.TrackRequest(
                    request.HeaderContext.SessionHash,
                    request.HeaderContext.SessionId,
                    snapshot
                );
        }
        catch
        { /* 预测永不阻塞主链路 */
        }

        // need05-02：前缀哈希（整包确定性哈希；稳定即 prompt cache 命中前提）
        string prefixHash = HeaderVariableResolver.ComputeDeterministicHash16(requestPayload);
        try
        {
            // need09-02：sysHash → stableHash（stable 区）+ fullHash（整包 system 文本）双记；
            // PrefixHash 恒等于 fullHash，老 Bridge 对比行为不变。
            string fullSys = request.SystemInstructions ?? string.Empty;
            var (stablePart, _) = Services.PrefixCachePredictor.SplitSystem(fullSys);
            string stableHash = HeaderVariableResolver.ComputeDeterministicHash16(
                stablePart ?? string.Empty
            );
            string fullHash = HeaderVariableResolver.ComputeDeterministicHash16(fullSys);
            Services.PromptLastSentStore.Record(
                request.HeaderContext.SessionId,
                new Services.LastSentPromptSnapshot
                {
                    ReqId = request.RequestId,
                    SessionId = request.HeaderContext.SessionId,
                    WorkspaceId = request.HeaderContext.WorkspaceId,
                    PrefixHash = fullHash,
                    StableHash = stableHash,
                    FullHash = fullHash,
                    CharLength = fullSys.Length,
                    SentAtUtc = DateTime.UtcNow.ToString("o")
                }
            );
        }
        catch { }
        // 记录请求追踪日志
        LogRequestTrace(
            request,
            endpointUrl,
            prefixHash,
            predictedHit,
            predictedCached,
            divergenceBlock
        );

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
            response = await SendRequestAsync(client, request, endpointUrl, requestPayload, ct)
                .ConfigureAwait(false);

            // 针对部分老旧接口不识别 stream_options 返回 400 Bad Request 的自动降级重试机制
            if (
                response.StatusCode == HttpStatusCode.BadRequest
                && includeUsageInStream
                && request.Model.Protocol == LlmProtocolType.Chat
            )
            {
                HaloLogger.App.Warn(
                    $"[LlmGateway] Provider [{request.Provider.Name}] 返回 400 Bad Request，尝试降级移除 stream_options 重试"
                );
                RejectUsageFeatureProviders[request.Provider.Id] = true;
                includeUsageInStream = false;
                requestPayload = adapter.SerializeRequest(request, includeUsageInStream: false);

                response.Dispose();
                response = await SendRequestAsync(client, request, endpointUrl, requestPayload, ct)
                    .ConfigureAwait(false);
            }

            // need17-01 Prefer：payload 含 strict 且网关 400 明确提及 strict/unknown keyword 时，
            // 去 strict 重发一次并 Warn（仅一次，不循环；Require/Off 由配置层控制是否发送）。
            if (
                response.StatusCode == HttpStatusCode.BadRequest
                && requestPayload.Contains("\"strict\"")
            )
            {
                string peekBody = await PeekErrorBodyAsync(response, ct).ConfigureAwait(false);
                if (IsStrictRejection(peekBody))
                {
                    HaloLogger.App.Warn(
                        $"[LlmGateway] Provider [{request.Provider.Name}] 不支持 strict 工具声明（400 提及 strict），已去 strict 重发一次"
                    );
                    requestPayload = StripStrictFromPayload(requestPayload);
                    response.Dispose();
                    response = await SendRequestAsync(
                            client,
                            request,
                            endpointUrl,
                            requestPayload,
                            ct
                        )
                        .ConfigureAwait(false);
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
                LogResponseTrace(
                    request,
                    finalStatusCode,
                    stopwatch.ElapsedMilliseconds,
                    ttftMs,
                    inTokens,
                    outTokens,
                    cachedTokens,
                    totalTokens,
                    toolCallCount,
                    toolNames
                );
                // need22-P1 T2：取消后不再写旧事件（错误码 HTTP_{code} 照常透传给未取消的调用方）。
                if (!ct.IsCancellationRequested)
                    await writer
                        .WriteAsync(
                            UnifiedStreamEvent.Error($"HTTP_{finalStatusCode}", formattedError),
                            ct
                        )
                        .ConfigureAwait(false);
                return;
            }

            var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await foreach (
                var evt in adapter.ParseStreamAsync(responseStream, ct).ConfigureAwait(false)
            )
            {
                if (
                    !ttftMs.HasValue
                    && (
                        evt.Kind == UnifiedStreamEventKind.TextDelta
                        || evt.Kind == UnifiedStreamEventKind.ReasoningDelta
                        || evt.Kind == UnifiedStreamEventKind.ToolCallStarted
                    )
                )
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
            LogResponseTrace(
                request,
                finalStatusCode,
                stopwatch.ElapsedMilliseconds,
                ttftMs,
                inTokens,
                outTokens,
                cachedTokens,
                totalTokens,
                toolCallCount,
                toolNames,
                predictedHit,
                predictedCached,
                divergenceBlock
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogResponseTrace(
                request,
                499,
                stopwatch.ElapsedMilliseconds,
                ttftMs,
                inTokens,
                outTokens,
                cachedTokens,
                totalTokens,
                toolCallCount,
                toolNames
            );
            // need22-P1 T2：读端已随 ct 退出，此处不再补写 Cancelled（TryComplete 即 enough）。
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"[LlmGateway] 调用异常 ({request.Provider.Name} / {request.Model.Id})",
                ex
            );
            LogResponseTrace(
                request,
                500,
                stopwatch.ElapsedMilliseconds,
                ttftMs,
                inTokens,
                outTokens,
                cachedTokens,
                totalTokens,
                toolCallCount,
                toolNames
            );
            // need22-P1 T3：网关侧超时（HttpClient.Timeout/传输超时且非用户取消）透传 TIMEOUT 码，其它异常沿用 GATEWAY_EXCEPTION。
            string code = IsTimeoutException(ex) ? "TIMEOUT" : "GATEWAY_EXCEPTION";
            if (!ct.IsCancellationRequested)
                await writer
                    .WriteAsync(UnifiedStreamEvent.Error(code, ex.Message), ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
