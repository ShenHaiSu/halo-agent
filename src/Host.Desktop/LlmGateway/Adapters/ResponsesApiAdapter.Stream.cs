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

public sealed partial class ResponsesApiAdapter : ILlmProtocolAdapter
{
    public async IAsyncEnumerable<UnifiedStreamEvent> ParseStreamAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        yield return UnifiedStreamEvent.Started();

        var functionCallItems = new Dictionary<string, RespFunctionCallState>(
            StringComparer.Ordinal
        );
        bool streamCompletedEmitted = false;
        bool respIncomplete = false; // NEED17-06联调：response.status=incomplete 或 max_output_tokens 时截断保护（与 chat length 对齐）。

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
                int doneClosed = 0;
                foreach (var state in functionCallItems.Values)
                {
                    if (state.StartedEmitted && !state.CompletedEmitted)
                    {
                        yield return UnifiedStreamEvent.ToolDone(
                            state.CallId,
                            state.Name,
                            state.ArgumentsBuilder.ToString(),
                            state.ItemId
                        );
                        state.CompletedEmitted = true;
                        doneClosed++;
                    }
                }
                // NEED17-06联调：截断回复保护（与 chat length 对齐；该轮 Orchestrator 侧丢弃零执行）。
                if (respIncomplete && doneClosed > 0)
                {
                    yield return UnifiedStreamEvent.Truncated(functionCallItems.Count);
                }
                if (!streamCompletedEmitted)
                {
                    yield return UnifiedStreamEvent.Completed();
                    streamCompletedEmitted = true;
                }
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
                string eventType = frame.EventType ?? string.Empty;

                // 若 frame 中未携带显式 event: 头，尝试从 JSON 载荷中的 type 字段读取
                if (
                    string.IsNullOrEmpty(eventType) && root.TryGetProperty("type", out var typeElem)
                )
                {
                    eventType = typeElem.GetString() ?? string.Empty;
                }

                switch (eventType)
                {
                    case "response.created":
                        // 已在流开始时发出 Started，可记录或忽略
                        break;

                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta":
                        if (root.TryGetProperty("delta", out var reasonDeltaElem))
                        {
                            string? delta = reasonDeltaElem.GetString();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                yield return UnifiedStreamEvent.Reasoning(delta);
                            }
                        }
                        break;

                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var outDeltaElem))
                        {
                            string? delta = outDeltaElem.GetString();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                yield return UnifiedStreamEvent.Text(delta);
                            }
                        }
                        break;

                    case "response.output_item.added":
                        if (root.TryGetProperty("item", out var itemElem))
                        {
                            string? itemType = itemElem.TryGetProperty("type", out var it)
                                ? it.GetString()
                                : null;
                            if (
                                string.Equals(
                                    itemType,
                                    "function_call",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                string itemId = itemElem.TryGetProperty("id", out var idElem)
                                    ? idElem.GetString() ?? string.Empty
                                    : string.Empty;
                                string callId = itemElem.TryGetProperty("call_id", out var cidElem)
                                    ? cidElem.GetString() ?? itemId
                                    : itemId;
                                string funcName = itemElem.TryGetProperty("name", out var fnElem)
                                    ? fnElem.GetString() ?? string.Empty
                                    : string.Empty;

                                var state = new RespFunctionCallState
                                {
                                    ItemId = itemId,
                                    CallId = callId,
                                    Name = funcName,
                                    StartedEmitted = true
                                };
                                functionCallItems[itemId] = state;
                                yield return UnifiedStreamEvent.ToolStarted(
                                    callId,
                                    funcName,
                                    itemId
                                );
                            }
                        }
                        break;

                    case "response.function_call_arguments.delta":
                        {
                            string itemId = root.TryGetProperty("item_id", out var idElem)
                                ? idElem.GetString() ?? string.Empty
                                : string.Empty;
                            if (!functionCallItems.TryGetValue(itemId, out var state))
                            {
                                string callId = root.TryGetProperty("call_id", out var cid)
                                    ? cid.GetString() ?? itemId
                                    : itemId;
                                state = new RespFunctionCallState
                                {
                                    ItemId = itemId,
                                    CallId = callId
                                };
                                functionCallItems[itemId] = state;
                            }

                            if (root.TryGetProperty("delta", out var deltaElem))
                            {
                                string? delta = deltaElem.GetString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    state.ArgumentsBuilder.Append(delta);
                                    yield return UnifiedStreamEvent.ToolArgs(
                                        state.CallId,
                                        delta,
                                        state.ItemId
                                    );
                                }
                            }
                        }
                        break;

                    case "response.function_call_arguments.done":
                        {
                            string itemId = root.TryGetProperty("item_id", out var idElem)
                                ? idElem.GetString() ?? string.Empty
                                : string.Empty;
                            if (!functionCallItems.TryGetValue(itemId, out var state))
                            {
                                string callId = root.TryGetProperty("call_id", out var cid)
                                    ? cid.GetString() ?? itemId
                                    : itemId;
                                state = new RespFunctionCallState
                                {
                                    ItemId = itemId,
                                    CallId = callId
                                };
                                functionCallItems[itemId] = state;
                            }

                            string finalArgs = root.TryGetProperty("arguments", out var argElem)
                                ? argElem.GetString() ?? state.ArgumentsBuilder.ToString()
                                : state.ArgumentsBuilder.ToString();

                            state.CompletedEmitted = true;
                            yield return UnifiedStreamEvent.ToolDone(
                                state.CallId,
                                state.Name,
                                finalArgs,
                                state.ItemId
                            );
                        }
                        break;

                    case "response.output_item.done":
                        if (root.TryGetProperty("item", out var doneItemElem))
                        {
                            string? itemType = doneItemElem.TryGetProperty("type", out var it)
                                ? it.GetString()
                                : null;
                            if (
                                string.Equals(
                                    itemType,
                                    "function_call",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                string itemId = doneItemElem.TryGetProperty("id", out var idElem)
                                    ? idElem.GetString() ?? string.Empty
                                    : string.Empty;
                                if (
                                    functionCallItems.TryGetValue(itemId, out var state)
                                    && !state.CompletedEmitted
                                )
                                {
                                    string finalArgs = doneItemElem.TryGetProperty(
                                        "arguments",
                                        out var argElem
                                    )
                                        ? argElem.GetString() ?? state.ArgumentsBuilder.ToString()
                                        : state.ArgumentsBuilder.ToString();

                                    state.CompletedEmitted = true;
                                    yield return UnifiedStreamEvent.ToolDone(
                                        state.CallId,
                                        state.Name,
                                        finalArgs,
                                        state.ItemId
                                    );
                                }
                            }
                        }
                        break;

                    case "response.completed":
                        JsonElement usageElem = default;
                        bool hasUsage = false;
                        if (
                            root.TryGetProperty("response", out var respElem)
                            && respElem.ValueKind == JsonValueKind.Object
                            && respElem.TryGetProperty("usage", out var u1)
                            && u1.ValueKind == JsonValueKind.Object
                        )
                        {
                            usageElem = u1;
                            hasUsage = true;
                        }
                        else if (
                            root.TryGetProperty("usage", out var u2)
                            && u2.ValueKind == JsonValueKind.Object
                        )
                        {
                            usageElem = u2;
                            hasUsage = true;
                        }

                        if (hasUsage)
                        {
                            int inTokens = TryGetInt32(usageElem, "input_tokens");
                            int outTokens = TryGetInt32(usageElem, "output_tokens");
                            int totalTokens = TryGetInt32(
                                usageElem,
                                "total_tokens",
                                inTokens + outTokens
                            );

                            int cachedTokens = 0;
                            if (
                                (
                                    usageElem.TryGetProperty("input_tokens_details", out var itd)
                                    || usageElem.TryGetProperty("input_token_details", out itd)
                                )
                                && itd.ValueKind == JsonValueKind.Object
                                && itd.TryGetProperty("cached_tokens", out var ctVal)
                            )
                            {
                                cachedTokens = GetInt32Value(ctVal);
                            }

                            yield return UnifiedStreamEvent.Tokens(
                                inTokens,
                                outTokens,
                                cachedTokens,
                                totalTokens
                            );
                        }
                        // NEED17-06联调：response.status=incomplete 或 incomplete_details.reason=max_output_tokens → 截断保护。
                        if (
                            root.TryGetProperty("response", out var stElem)
                            && stElem.ValueKind == JsonValueKind.Object
                        )
                        {
                            string? status = stElem.TryGetProperty("status", out var stVal)
                                ? stVal.GetString()
                                : null;
                            string? reason =
                                stElem.TryGetProperty("incomplete_details", out var ind)
                                && ind.ValueKind == JsonValueKind.Object
                                && ind.TryGetProperty("reason", out var rsn)
                                    ? rsn.GetString()
                                    : null;
                            if (
                                string.Equals(
                                    status,
                                    "incomplete",
                                    StringComparison.OrdinalIgnoreCase
                                )
                                || string.Equals(
                                    reason,
                                    "max_output_tokens",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                respIncomplete = true;
                            }
                        }
                        yield return UnifiedStreamEvent.Completed();
                        streamCompletedEmitted = true;
                        break;
                    case "response.incomplete":
                        // NEED17-06联调：服务端显式截断事件；工具调用由兜底闭合后补 Truncated（见 [DONE]/文末兜底）。
                        respIncomplete = true;
                        break;
                    case "response.failed":
                    case "error":
                    {
                        string code = "RESPONSES_API_ERROR";
                        string message = "Responses API 执行失败";

                        if (
                            root.TryGetProperty("error", out var errElem)
                            && errElem.ValueKind == JsonValueKind.Object
                        )
                        {
                            code = errElem.TryGetProperty("code", out var c)
                                ? c.GetString() ?? code
                                : code;
                            message = errElem.TryGetProperty("message", out var m)
                                ? m.GetString() ?? message
                                : message;
                        }

                        yield return UnifiedStreamEvent.Error(code, message);
                        yield break;
                    }
                }
            }
        }

        // 兜底闭合未完成的工具调用
        int tailClosed = 0;
        foreach (var state in functionCallItems.Values)
        {
            if (state.StartedEmitted && !state.CompletedEmitted)
            {
                yield return UnifiedStreamEvent.ToolDone(
                    state.CallId,
                    state.Name,
                    state.ArgumentsBuilder.ToString(),
                    state.ItemId
                );
                state.CompletedEmitted = true;
                tailClosed++;
            }
        }
        // NEED17-06联调：截断回复保护（与 chat length 对齐）。
        if (respIncomplete && tailClosed > 0)
        {
            yield return UnifiedStreamEvent.Truncated(functionCallItems.Count);
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

        string hint = string.Empty;
        if (statusCode == 404)
        {
            hint =
                " [诊断建议]: 上游接口返回 404 (/v1/responses)，该 Provider 可能尚未支持 OpenAI Responses 协议，建议在模型设置中切换为 Chat 协议重试。";
        }

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

                return $"[OpenAI Responses] 上游接口返回 HTTP {statusCode} ({type} {code}): {msg}{hint}".Trim();
            }
        }
        catch
        {
            // 忽略非 JSON 解析异常
        }

        return $"[OpenAI Responses] 上游接口返回 HTTP {statusCode}: {raw}{hint}";
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

    private sealed class RespFunctionCallState
    {
        public string ItemId { get; init; } = string.Empty;
        public string CallId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder ArgumentsBuilder { get; } = new();
        public bool StartedEmitted { get; set; }
        public bool CompletedEmitted { get; set; }
    }
}
