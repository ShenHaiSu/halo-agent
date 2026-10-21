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

public sealed partial class LlmGatewayClient : ILlmGatewayClient
{
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
                payload,
                @",\s*""strict""\s*:\s*(true|false)",
                string.Empty
            );
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"""strict""\s*:\s*(true|false)\s*,",
                string.Empty
            );
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

    private static async Task<string> PeekErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
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
    internal static Func<int, CancellationToken, Task> RetryDelayAsync = (ms, ct) =>
        Task.Delay(ms, ct);

    /// <summary>need22-P1 T3：网关超时判定（HttpClient.Timeout 抛 TaskCanceledException；ct 未取消时即超时）。</summary>
    internal static bool IsTimeoutException(Exception? ex)
    {
        if (ex == null)
            return false;
        if (ex is TimeoutException)
            return true;
        if (ex is TaskCanceledException)
            return true;
        if (
            ex is IOException io
            && io.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        )
            return true;
        return false;
    }

    private static bool IsTransientTransportError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        return ex is TaskCanceledException
            || ex is TimeoutException
            || ex is HttpRequestException
            || ex is IOException;
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
        CancellationToken ct
    )
    {
        HttpResponseMessage? retryableResponse = null;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 0)
                {
                    int baseMs =
                        attempt == 1
                            ? 1000
                            : attempt == 2
                                ? 2000
                                : 4000;
                    int delayMs = baseMs + Random.Shared.Next(0, 500);
                    await RetryDelayAsync(delayMs, ct).ConfigureAwait(false);
                }
                retryableResponse?.Dispose();
                retryableResponse = null;
                HttpResponseMessage response;
                try
                {
                    response = await SendOnceAsync(client, request, endpointUrl, requestPayload, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransientTransportError(ex, ct))
                {
                    HaloLogger.App.Warn(
                        $"[LlmGateway] 传输异常可重试（第{attempt + 1}/4 次）：{ex.GetType().Name} {ex.Message}"
                    );
                    if (attempt >= 3)
                        throw;
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
        CancellationToken ct
    )
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
        HeaderVariableResolver.ApplyCustomHeaders(
            httpRequest,
            request.Provider,
            request.HeaderContext
        );

        return await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private void LogRequestTrace(
        UnifiedLlmRequest request,
        string endpointUrl,
        string? prefixHash = null,
        double? predictedHit = null,
        int predictedCached = 0,
        string? divergenceBlock = null
    )
    {
        try
        {
            string proxySummary = GetProxySummary(request.Provider);
            string headersSummary = GetCustomHeadersSummary(
                request.Provider,
                request.HeaderContext
            );
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
                    if (omitted > 0)
                        imgTruncated = true;
                }
            }
            int skillL0 = 0,
                skillTokens = 0,
                memPinned = 0,
                memTokens = 0;
            try
            {
                var (stableText, _) = Services.PrefixCachePredictor.SplitSystem(
                    request.SystemInstructions
                );
                var (_, skillPart, memPart) = Services.PrefixCachePredictor.SplitStableSystem(
                    stableText
                );
                skillL0 = CountStableIndexRows(skillPart);
                skillTokens = Services.TokenBudgetService.EstimateTokens(skillPart);
                memPinned = CountStableMemorySections(memPart);
                memTokens = Services.TokenBudgetService.EstimateTokens(memPart);
            }
            catch
            { /* 统计永不阻塞主链路 */
            }

            string predText = predictedHit.HasValue ? $"{predictedHit.Value * 100.0:F1}%" : "-";
            string line =
                $"[REQ] reqId={request.RequestId} | session={request.HeaderContext.SessionId} (hash={request.HeaderContext.SessionHash}) | provider={request.Provider.Name} | model={request.Model.Id} | protocol={request.Model.Protocol.ToString().ToUpperInvariant()} | url={endpointUrl} | proxy={proxySummary} | customHeaders=[{headersSummary}] | prefixHash={prefixHash ?? "-"} | msgCount={msgCount} | inputEst={inputEst} | predictedHit={predText} (cached~{predictedCached}, div={divergenceBlock ?? "-"}) | skillL0={skillL0} skillTokens={skillTokens} memPinned={memPinned} memTokens={memTokens} imgCount={imgCount} imgTruncated={imgTruncated}";
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
        if (string.IsNullOrEmpty(skillPart))
            return 0;
        int n = 0;
        foreach (var line in skillPart.Split('\n'))
            if (line.TrimStart().StartsWith("- [", StringComparison.Ordinal))
                n++;
        return n;
    }

    /// <summary>need09-02：统计 pinned memory 节数（行首 `### ` 的行）。</summary>
    private static int CountStableMemorySections(string? memPart)
    {
        if (string.IsNullOrEmpty(memPart))
            return 0;
        int n = 0;
        foreach (var line in memPart.Split('\n'))
            if (line.TrimStart().StartsWith("### ", StringComparison.Ordinal))
                n++;
        return n;
    }

    private static void RecordPredictionActual(
        UnifiedLlmRequest request,
        int inTokens,
        int cachedTokens
    )
    {
        try
        {
            if (inTokens > 0)
                Services.PrefixCachePredictor.RecordActual(
                    request.HeaderContext.SessionHash,
                    request.HeaderContext.SessionId,
                    inTokens,
                    cachedTokens
                );
        }
        catch
        { /* 预测存储永不抛 */
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
        HashSet<string> toolNames,
        double? predictedHit = null,
        int predictedCached = 0,
        string? divergenceBlock = null
    )
    {
        try
        {
            double hitRate = (inTokens > 0) ? ((double)cachedTokens / inTokens * 100.0) : 0.0;
            string toolCallsText =
                toolCallCount > 0 ? $"{toolCallCount}({string.Join(",", toolNames)})" : "0";

            long effectiveTtft = ttftMs ?? totalMs;
            string predText = predictedHit.HasValue ? $"{predictedHit.Value * 100.0:F1}%" : "-";
            string line =
                $"[RES] reqId={request.RequestId} | status={statusCode} | ttftMs={effectiveTtft} | totalMs={totalMs} | inTokens={inTokens} (cached={cachedTokens}, hitRate={hitRate:F1}%) | outTokens={outTokens} | toolCalls={toolCallsText} | predictedHit={predText} (cached~{predictedCached}, div={divergenceBlock ?? "-"})";
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
        string fullLine =
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] {line}{Environment.NewLine}";

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
            ProviderProxyMode.Custom when provider.CustomProxy != null
                => $"CUSTOM({provider.CustomProxy.Scheme}://{provider.CustomProxy.Host}:{provider.CustomProxy.Port})",
            _ => "SYSTEM(Default)"
        };
    }

    private static string GetCustomHeadersSummary(
        ProviderConfig provider,
        HeaderResolutionContext context
    )
    {
        if (
            !provider.EnableCustomHeaders
            || provider.CustomHeaders == null
            || provider.CustomHeaders.Count == 0
        )
        {
            return string.Empty;
        }

        var list = new List<string>();
        foreach (var h in provider.CustomHeaders)
        {
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Key))
            {
                string resolved = HeaderVariableResolver.ResolveTemplate(
                    h.ValueTemplate ?? string.Empty,
                    context
                );
                list.Add($"{h.Key.Trim()}={resolved}");
            }
        }

        return string.Join(", ", list);
    }
}
