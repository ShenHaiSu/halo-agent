using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.LlmGateway.Sse;

namespace Host.Desktop.LlmGateway.Adapters;

/// <summary>
/// OpenAI Responses API 协议适配器 (/v1/responses)
/// 覆盖需求 #2 与 #7：处理 instructions、扁平化 input 多态列表、扁平化 tools 以及语义化 SSE 事件状态机。
/// </summary>
public sealed class ResponsesApiAdapter : ILlmProtocolAdapter
{
    public LlmProtocolType Protocol => LlmProtocolType.Resp;

    public string ResolveEndpointUrl(string baseUrl) =>
        HeaderVariableResolver.ResolveEndpointUrl(baseUrl, LlmProtocolType.Resp);

    public string SerializeRequest(UnifiedLlmRequest request, bool includeUsageInStream = true)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            // 1. 模型标识
            writer.WriteString("model", request.Model.Id);

            // 2. 流式开关
            writer.WriteBoolean("stream", true);

            // 3. 系统提示词 (顶层 instructions 字段)
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            {
                writer.WriteString("instructions", request.SystemInstructions);
            }

            // 4. 最大输出 Token (max_output_tokens 字段)
            if (request.Model.MaxOutputTokens > 0)
            {
                writer.WriteNumber("max_output_tokens", request.Model.MaxOutputTokens);
            }

            // 5. 采样温度
            if (request.Model.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Model.Temperature.Value);
            }

            // 6. 扁平化多态 input 数组
            writer.WriteStartArray("input");
            foreach (var msg in request.Messages)
            {
                if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "user");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                else if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(msg.Content))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "message");
                        writer.WriteString("role", "assistant");
                        writer.WriteStartArray("content");
                        writer.WriteStartObject();
                        writer.WriteString("type", "output_text");
                        writer.WriteString("text", msg.Content);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        foreach (var tc in msg.ToolCalls)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "function_call");
                            writer.WriteString("call_id", tc.CallId);
                            writer.WriteString("name", tc.FunctionName);
                            writer.WriteString("arguments", tc.ArgumentsJson ?? "{}");
                            writer.WriteEndObject();
                        }
                    }
                }
                else if (msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", msg.ToolCallId ?? string.Empty);
                    writer.WriteString("output", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                }
                else if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    // 若有额外 system 消息，作为 developer message 追加至 input
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "developer");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray(); // input

            // 7. 扁平化工具定义列表（非嵌套式）
            if (request.Tools != null && request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.ParametersSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async IAsyncEnumerable<UnifiedStreamEvent> ParseStreamAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return UnifiedStreamEvent.Started();

        var functionCallItems = new Dictionary<string, RespFunctionCallState>(StringComparer.Ordinal);
        bool streamCompletedEmitted = false;

        await foreach (var frame in SseStreamReader.ReadFramesAsync(responseStream, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(frame.Data))
            {
                continue;
            }

            string data = frame.Data.Trim();
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var state in functionCallItems.Values)
                {
                    if (state.StartedEmitted && !state.CompletedEmitted)
                    {
                        yield return UnifiedStreamEvent.ToolDone(state.CallId, state.Name, state.ArgumentsBuilder.ToString(), state.ItemId);
                        state.CompletedEmitted = true;
                    }
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
                if (string.IsNullOrEmpty(eventType) && root.TryGetProperty("type", out var typeElem))
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
                            string? itemType = itemElem.TryGetProperty("type", out var it) ? it.GetString() : null;
                            if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                            {
                                string itemId = itemElem.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                                string callId = itemElem.TryGetProperty("call_id", out var cidElem) ? cidElem.GetString() ?? itemId : itemId;
                                string funcName = itemElem.TryGetProperty("name", out var fnElem) ? fnElem.GetString() ?? string.Empty : string.Empty;

                                var state = new RespFunctionCallState
                                {
                                    ItemId = itemId,
                                    CallId = callId,
                                    Name = funcName,
                                    StartedEmitted = true
                                };
                                functionCallItems[itemId] = state;
                                yield return UnifiedStreamEvent.ToolStarted(callId, funcName, itemId);
                            }
                        }
                        break;

                    case "response.function_call_arguments.delta":
                        {
                            string itemId = root.TryGetProperty("item_id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                            if (!functionCallItems.TryGetValue(itemId, out var state))
                            {
                                string callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? itemId : itemId;
                                state = new RespFunctionCallState { ItemId = itemId, CallId = callId };
                                functionCallItems[itemId] = state;
                            }

                            if (root.TryGetProperty("delta", out var deltaElem))
                            {
                                string? delta = deltaElem.GetString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    state.ArgumentsBuilder.Append(delta);
                                    yield return UnifiedStreamEvent.ToolArgs(state.CallId, delta, state.ItemId);
                                }
                            }
                        }
                        break;

                    case "response.function_call_arguments.done":
                        {
                            string itemId = root.TryGetProperty("item_id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                            if (!functionCallItems.TryGetValue(itemId, out var state))
                            {
                                string callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? itemId : itemId;
                                state = new RespFunctionCallState { ItemId = itemId, CallId = callId };
                                functionCallItems[itemId] = state;
                            }

                            string finalArgs = root.TryGetProperty("arguments", out var argElem)
                                ? argElem.GetString() ?? state.ArgumentsBuilder.ToString()
                                : state.ArgumentsBuilder.ToString();

                            state.CompletedEmitted = true;
                            yield return UnifiedStreamEvent.ToolDone(state.CallId, state.Name, finalArgs, state.ItemId);
                        }
                        break;

                    case "response.output_item.done":
                        if (root.TryGetProperty("item", out var doneItemElem))
                        {
                            string? itemType = doneItemElem.TryGetProperty("type", out var it) ? it.GetString() : null;
                            if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                            {
                                string itemId = doneItemElem.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                                if (functionCallItems.TryGetValue(itemId, out var state) && !state.CompletedEmitted)
                                {
                                    string finalArgs = doneItemElem.TryGetProperty("arguments", out var argElem)
                                        ? argElem.GetString() ?? state.ArgumentsBuilder.ToString()
                                        : state.ArgumentsBuilder.ToString();

                                    state.CompletedEmitted = true;
                                    yield return UnifiedStreamEvent.ToolDone(state.CallId, state.Name, finalArgs, state.ItemId);
                                }
                            }
                        }
                        break;

                    case "response.completed":
                        JsonElement usageElem = default;
                        bool hasUsage = false;
                        if (root.TryGetProperty("response", out var respElem) &&
                            respElem.TryGetProperty("usage", out var u1))
                        {
                            usageElem = u1;
                            hasUsage = true;
                        }
                        else if (root.TryGetProperty("usage", out var u2))
                        {
                            usageElem = u2;
                            hasUsage = true;
                        }

                        if (hasUsage)
                        {
                            int inTokens = usageElem.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                            int outTokens = usageElem.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                            int totalTokens = usageElem.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : (inTokens + outTokens);

                            int cachedTokens = 0;
                            if ((usageElem.TryGetProperty("input_tokens_details", out var itd) ||
                                 usageElem.TryGetProperty("input_token_details", out itd)) &&
                                itd.TryGetProperty("cached_tokens", out var ctVal))
                            {
                                cachedTokens = ctVal.GetInt32();
                            }

                            yield return UnifiedStreamEvent.Tokens(inTokens, outTokens, cachedTokens, totalTokens);
                        }

                        yield return UnifiedStreamEvent.Completed();
                        streamCompletedEmitted = true;
                        break;

                    case "response.failed":
                    case "error":
                        {
                            string code = "RESPONSES_API_ERROR";
                            string message = "Responses API 执行失败";

                            if (root.TryGetProperty("error", out var errElem))
                            {
                                code = errElem.TryGetProperty("code", out var c) ? c.GetString() ?? code : code;
                                message = errElem.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
                            }

                            yield return UnifiedStreamEvent.Error(code, message);
                            yield break;
                        }
                }
            }
        }

        // 兜底闭合未完成的工具调用
        foreach (var state in functionCallItems.Values)
        {
            if (state.StartedEmitted && !state.CompletedEmitted)
            {
                yield return UnifiedStreamEvent.ToolDone(state.CallId, state.Name, state.ArgumentsBuilder.ToString(), state.ItemId);
                state.CompletedEmitted = true;
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

        string hint = string.Empty;
        if (statusCode == 404)
        {
            hint = " [诊断建议]: 上游接口返回 404 (/v1/responses)，该 Provider 可能尚未支持 OpenAI Responses 协议，建议在模型设置中切换为 Chat 协议重试。";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var errorElem))
            {
                string msg = errorElem.TryGetProperty("message", out var m) ? m.GetString() ?? raw : raw;
                string type = errorElem.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                string code = errorElem.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;

                return $"[OpenAI Responses] 上游接口返回 HTTP {statusCode} ({type} {code}): {msg}{hint}".Trim();
            }
        }
        catch
        {
            // 忽略非 JSON 解析异常
        }

        return $"[OpenAI Responses] 上游接口返回 HTTP {statusCode}: {raw}{hint}";
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
