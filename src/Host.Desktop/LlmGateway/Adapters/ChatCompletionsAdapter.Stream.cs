using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.LlmGateway.Sse;

namespace Host.Desktop.LlmGateway.Adapters;

public sealed partial class ChatCompletionsAdapter : ILlmProtocolAdapter
{
    public async IAsyncEnumerable<UnifiedStreamEvent> ParseStreamAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        yield return UnifiedStreamEvent.Started();

        var toolCallStates = new SortedDictionary<int, PartialToolCallState>();
        bool streamCompletedEmitted = false;

        await foreach (
            var frame in SseStreamReader.ReadFramesAsync(responseStream, ct).ConfigureAwait(false)
        )
        {
            if (string.IsNullOrWhiteSpace(frame.Data))
            {
                continue;
            }

            string data = frame.Data.Trim();
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                // 完成未闭合的工具调用
                foreach (var state in toolCallStates.Values)
                {
                    if (state.StartedEventEmitted && !state.CompletedEventEmitted)
                    {
                        yield return UnifiedStreamEvent.ToolDone(
                            state.CallId,
                            state.FunctionName,
                            state.ArgumentsBuilder.ToString()
                        );
                        state.CompletedEventEmitted = true;
                    }
                }

                yield return UnifiedStreamEvent.Completed();
                streamCompletedEmitted = true;
                yield break;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // 1. 检查是否存在上游错误载荷（兼容 "error":null，避免对 Null 调用 TryGetProperty 抛 InvalidOperationException）
                if (
                    root.TryGetProperty("error", out var errorElem)
                    && errorElem.ValueKind == JsonValueKind.Object
                )
                {
                    string errMessage = errorElem.TryGetProperty("message", out var msgElem)
                        ? msgElem.GetString() ?? "未知上游错误"
                        : "未知上游错误";
                    string errCode = errorElem.TryGetProperty("code", out var codeElem)
                        ? codeElem.GetString() ?? "UPSTREAM_ERROR"
                        : "UPSTREAM_ERROR";

                    yield return UnifiedStreamEvent.Error(errCode, errMessage);
                    yield break;
                }

                // 2. 检查顶层 usage（兼容上游返回 "usage":null 的中间 chunk，避免 InvalidOperationException 导致整轮会话失败）
                if (
                    root.TryGetProperty("usage", out var usageElem)
                    && usageElem.ValueKind == JsonValueKind.Object
                )
                {
                    int inTokens = TryGetInt32(usageElem, "prompt_tokens");
                    int outTokens = TryGetInt32(usageElem, "completion_tokens");
                    int totalTokens = TryGetInt32(usageElem, "total_tokens", inTokens + outTokens);

                    int cachedTokens = 0;
                    if (
                        usageElem.TryGetProperty("prompt_tokens_details", out var ptd)
                        && ptd.ValueKind == JsonValueKind.Object
                        && ptd.TryGetProperty("cached_tokens", out var ctVal)
                    )
                    {
                        cachedTokens = GetInt32Value(ctVal);
                    }
                    else if (usageElem.TryGetProperty("prompt_cache_hit_tokens", out var pcht))
                    {
                        cachedTokens = GetInt32Value(pcht);
                    }

                    yield return UnifiedStreamEvent.Tokens(
                        inTokens,
                        outTokens,
                        cachedTokens,
                        totalTokens
                    );
                }
                // 3. 检查 choices 增量（兼容 "choices":null / 非数组，避免 GetArrayLength 抛异常）
                if (
                    root.TryGetProperty("choices", out var choicesElem)
                    && choicesElem.ValueKind == JsonValueKind.Array
                    && choicesElem.GetArrayLength() > 0
                )
                {
                    var choice = choicesElem[0];
                    if (choice.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (
                        choice.TryGetProperty("delta", out var deltaElem)
                        && deltaElem.ValueKind == JsonValueKind.Object
                    )
                    {
                        // 3.1 思考链增量（兼容 DeepSeek reasoning_content 与各家 reasoning 字段）
                        string? reasoning = null;
                        if (deltaElem.TryGetProperty("reasoning_content", out var rcElem))
                        {
                            reasoning = rcElem.GetString();
                        }
                        else if (deltaElem.TryGetProperty("reasoning", out var rElem))
                        {
                            reasoning = rElem.GetString();
                        }

                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            yield return UnifiedStreamEvent.Reasoning(reasoning);
                        }

                        // 3.2 正文文本增量
                        if (deltaElem.TryGetProperty("content", out var contentElem))
                        {
                            string? text = contentElem.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return UnifiedStreamEvent.Text(text);
                            }
                        }

                        // 3.3 分片工具调用
                        if (
                            deltaElem.TryGetProperty("tool_calls", out var toolCallsElem)
                            && toolCallsElem.ValueKind == JsonValueKind.Array
                        )
                        {
                            foreach (var tcElem in toolCallsElem.EnumerateArray())
                            {
                                if (tcElem.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                int index = TryGetInt32(tcElem, "index");
                                if (!toolCallStates.TryGetValue(index, out var state))
                                {
                                    state = new PartialToolCallState { Index = index };
                                    toolCallStates[index] = state;
                                }

                                if (tcElem.TryGetProperty("id", out var idElem))
                                {
                                    string? id = idElem.GetString();
                                    if (!string.IsNullOrEmpty(id))
                                    {
                                        state.CallId = id;
                                    }
                                }

                                if (
                                    tcElem.TryGetProperty("function", out var funcElem)
                                    && funcElem.ValueKind == JsonValueKind.Object
                                )
                                {
                                    if (funcElem.TryGetProperty("name", out var nameElem))
                                    {
                                        string? name = nameElem.GetString();
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            state.FunctionName = name;
                                        }
                                    }

                                    if (
                                        !state.StartedEventEmitted
                                        && !string.IsNullOrEmpty(state.CallId)
                                        && !string.IsNullOrEmpty(state.FunctionName)
                                    )
                                    {
                                        yield return UnifiedStreamEvent.ToolStarted(
                                            state.CallId,
                                            state.FunctionName
                                        );
                                        state.StartedEventEmitted = true;
                                    }

                                    if (funcElem.TryGetProperty("arguments", out var argsElem))
                                    {
                                        string? argsChunk = argsElem.GetString();
                                        if (!string.IsNullOrEmpty(argsChunk))
                                        {
                                            state.ArgumentsBuilder.Append(argsChunk);
                                            yield return UnifiedStreamEvent.ToolArgs(
                                                state.CallId,
                                                argsChunk
                                            );
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 3.4 检查 finish_reason
                    if (choice.TryGetProperty("finish_reason", out var finishElem))
                    {
                        string? finishReason = finishElem.GetString();
                        if (
                            string.Equals(
                                finishReason,
                                "tool_calls",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            foreach (var state in toolCallStates.Values)
                            {
                                if (state.StartedEventEmitted && !state.CompletedEventEmitted)
                                {
                                    yield return UnifiedStreamEvent.ToolDone(
                                        state.CallId,
                                        state.FunctionName,
                                        state.ArgumentsBuilder.ToString()
                                    );
                                    state.CompletedEventEmitted = true;
                                }
                            }
                        }
                        // NEED17-06 §6.4：length → 先按现状闭合 ToolDone，再追加 StreamTruncated（含已收 toolCall 数）
                        else if (
                            string.Equals(
                                finishReason,
                                "length",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            foreach (var state in toolCallStates.Values)
                            {
                                if (state.StartedEventEmitted && !state.CompletedEventEmitted)
                                {
                                    yield return UnifiedStreamEvent.ToolDone(
                                        state.CallId,
                                        state.FunctionName,
                                        state.ArgumentsBuilder.ToString()
                                    );
                                    state.CompletedEventEmitted = true;
                                }
                            }
                            yield return UnifiedStreamEvent.Truncated(toolCallStates.Count);
                        }
                    }
                }
            }
        }

        // EOF 兜底闭合
        foreach (var state in toolCallStates.Values)
        {
            if (state.StartedEventEmitted && !state.CompletedEventEmitted)
            {
                yield return UnifiedStreamEvent.ToolDone(
                    state.CallId,
                    state.FunctionName,
                    state.ArgumentsBuilder.ToString()
                );
                state.CompletedEventEmitted = true;
            }
        }

        if (!streamCompletedEmitted)
        {
            yield return UnifiedStreamEvent.Completed();
        }
    }

    public string FormatErrorResponse(int statusCode, string responseBody)
    {
        string raw = string.IsNullOrWhiteSpace(responseBody)
            ? $"HTTP 状态码 {statusCode}"
            : (responseBody.Length > 2048 ? responseBody[..2048] : responseBody);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var errorElem)
                && errorElem.ValueKind == JsonValueKind.Object
            )
            {
                string msg = errorElem.TryGetProperty("message", out var m)
                    ? m.GetString() ?? raw
                    : raw;
                string type = errorElem.TryGetProperty("type", out var t)
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                string code = errorElem.TryGetProperty("code", out var c)
                    ? c.GetString() ?? string.Empty
                    : string.Empty;

                return $"[OpenAI Chat] 上游接口返回 HTTP {statusCode} ({type} {code}): {msg}".Trim();
            }
        }
        catch
        {
            // 非 JSON 格式，直接返回截断报文
        }

        return $"[OpenAI Chat] 上游接口返回 HTTP {statusCode}: {raw}";
    }

    private static int TryGetInt32(JsonElement parent, string propertyName, int defaultValue = 0)
    {
        if (
            parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out var elem)
        )
        {
            return defaultValue;
        }

        return GetInt32Value(elem, defaultValue);
    }

    private static int GetInt32Value(JsonElement elem, int defaultValue = 0)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.Number when elem.TryGetInt32(out int v) => v,
            JsonValueKind.Number => (int)elem.GetDouble(),
            JsonValueKind.String when int.TryParse(elem.GetString(), out int sv) => sv,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// S3 §4.3：本批 tool 附图里有几张被「超 MaxImagesPerTurn」丢掉。
    /// Planner 只从最早处丢，故「原始收集到的张数 - 实发张数」即本批丢失数。
    /// </summary>
    internal static int CountDroppedImages(int collectedCount, int sentCount)
    {
        int diff = collectedCount - sentCount;
        return diff > 0 ? diff : 0;
    }

    /// <summary>tool 块尾随 user 文案：带 callId 归属，单条截断 200 字防 token 翻倍。
    /// S3 §4.3：droppedCount&gt;0 时追加「另有 M 张因超 MaxImagesPerTurn(N)未发送」，让模型知道丢了谁。
    /// </summary>
    internal static string BuildTrailingToolImageText(
        List<(string CallId, string? Name, string? Text)> refs,
        int imageCount,
        int droppedCount = 0,
        int maxImagesPerTurn = 0
    )
    {
        string suffix =
            droppedCount > 0
                ? Models.MultimodalPlanner.DropNotice.CountTruncated(
                    imageCount,
                    droppedCount,
                    maxImagesPerTurn
                )
                : string.Empty;
        if (refs == null || refs.Count == 0)
            return $"[附图] 以上工具回包附图共 {imageCount} 张。{suffix}";
        var parts = new List<string>();
        foreach (var (cid, name, text) in refs)
        {
            string label = string.IsNullOrWhiteSpace(cid)
                ? (name ?? "tool")
                : ((name ?? "tool") + " " + cid.Trim());
            string snippet = (text ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
            if (snippet.Length > 200)
                snippet = snippet.Substring(0, 200) + "…";
            parts.Add(string.IsNullOrEmpty(snippet) ? label : label + "：" + snippet);
            if (string.Join("；", parts).Length > 800)
                break;
        }
        return $"[附图] 以下为上述工具回包附图（共 {imageCount} 张）：{string.Join("；", parts)}{suffix}";
    }

    private sealed class PartialToolCallState
    {
        public int Index { get; init; }
        public string CallId { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder ArgumentsBuilder { get; } = new();
        public bool StartedEventEmitted { get; set; }
        public bool CompletedEventEmitted { get; set; }
    }
}
