using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.LlmGateway.Sse;

namespace Host.Desktop.LlmGateway.Adapters;

/// <summary>
/// OpenAI Chat Completions 协议适配器 (/v1/chat/completions)
/// 覆盖需求 #2 与 #7：处理经典 messages 数组、嵌套 tools 结构与 choices[0].delta 流式事件聚合。
/// </summary>
public sealed class ChatCompletionsAdapter : ILlmProtocolAdapter
{
    public LlmProtocolType Protocol => LlmProtocolType.Chat;

    public string ResolveEndpointUrl(string baseUrl) =>
        HeaderVariableResolver.ResolveEndpointUrl(baseUrl, LlmProtocolType.Chat);

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

            // 2. 流式开关与用量包含选项
            writer.WriteBoolean("stream", true);
            if (includeUsageInStream)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            // 3. 最大输出 Token
            if (request.Model.MaxOutputTokens > 0)
            {
                writer.WriteNumber("max_tokens", request.Model.MaxOutputTokens);
            }

            // 4. 采样温度
            if (request.Model.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Model.Temperature.Value);
            }

            // 5. 对话消息列表
            writer.WriteStartArray("messages");

            // 5.1 系统提示词 (System Prompt)
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", request.SystemInstructions);
                writer.WriteEndObject();
            }

            // 5.2 历史与当前消息（need09-05：含图 user 走 content 数组；无图保持 content:string 零抖动）
            var mmPlan = MultimodalPlanner.PlanTurn(request);
            string visionDetail = VisionDefaults.NormalizeDetail(request.Model.VisionDetail);
            if (mmPlan.Dropped > 0)
            {
                try { Host.Desktop.Infrastructure.Logging.HaloLogger.Llm.Info("[REQ] reqId=" + request.RequestId + " model=" + request.Model.Id + " imgTotal=" + mmPlan.Total + " imgTruncated=" + mmPlan.Dropped); } catch { }
            }
            int mmIndex = -1;
            foreach (var msg in request.Messages)
            {
                mmIndex++;
                var kept = mmPlan.Kept[mmIndex].Where(r => !string.IsNullOrWhiteSpace(r.DataUrl)).ToList();
                writer.WriteStartObject();
                writer.WriteString("role", msg.Role);
                if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    // 若有 ToolCalls，content 统一输出有效文本或 ""（兼容严格校验第三方网关）
                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        writer.WriteString("content", msg.Content ?? string.Empty);
                        writer.WriteStartArray("tool_calls");
                        foreach (var tc in msg.ToolCalls)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("id", tc.CallId);
                            writer.WriteString("type", "function");
                            writer.WriteStartObject("function");
                            writer.WriteString("name", tc.FunctionName);
                            writer.WriteString("arguments", tc.ArgumentsJson ?? "{}");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString("content", msg.Content ?? string.Empty);
                    }
                }
                else if (msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("tool_call_id", msg.ToolCallId ?? string.Empty);
                    writer.WriteString("content", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    if (kept.Count > 0)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "user");
                        writer.WriteStartArray("content");
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", msg.Content ?? string.Empty);
                        writer.WriteEndObject();
                        foreach (var r in kept)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "image_url");
                            writer.WriteStartObject("image_url");
                            writer.WriteString("url", r.DataUrl!);
                            writer.WriteString("detail", visionDetail);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    continue;
                }
                else if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && kept.Count > 0)
                {
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    foreach (var r in kept)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "image_url");
                        writer.WriteStartObject("image_url");
                        writer.WriteString("url", r.DataUrl!);
                        writer.WriteString("detail", visionDetail);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("content", msg.Content ?? string.Empty);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray(); // messages

            // 6. 工具定义列表（嵌套式）
            // need05-02：按 name 升序稳定排序后序列化（内容不动），保证请求前缀后段字节稳定
            if (request.Tools != null && request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.ParametersSchema.WriteTo(writer);
                    writer.WriteEndObject();
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

        var toolCallStates = new SortedDictionary<int, PartialToolCallState>();
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
                // 完成未闭合的工具调用
                foreach (var state in toolCallStates.Values)
                {
                    if (state.StartedEventEmitted && !state.CompletedEventEmitted)
                    {
                        yield return UnifiedStreamEvent.ToolDone(state.CallId, state.FunctionName, state.ArgumentsBuilder.ToString());
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
                if (root.TryGetProperty("error", out var errorElem) &&
                    errorElem.ValueKind == JsonValueKind.Object)
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
                if (root.TryGetProperty("usage", out var usageElem) &&
                    usageElem.ValueKind == JsonValueKind.Object)
                {
                    int inTokens = TryGetInt32(usageElem, "prompt_tokens");
                    int outTokens = TryGetInt32(usageElem, "completion_tokens");
                    int totalTokens = TryGetInt32(usageElem, "total_tokens", inTokens + outTokens);

                    int cachedTokens = 0;
                    if (usageElem.TryGetProperty("prompt_tokens_details", out var ptd) &&
                        ptd.ValueKind == JsonValueKind.Object &&
                        ptd.TryGetProperty("cached_tokens", out var ctVal))
                    {
                        cachedTokens = GetInt32Value(ctVal);
                    }
                    else if (usageElem.TryGetProperty("prompt_cache_hit_tokens", out var pcht))
                    {
                        cachedTokens = GetInt32Value(pcht);
                    }

                    yield return UnifiedStreamEvent.Tokens(inTokens, outTokens, cachedTokens, totalTokens);
                }
                // 3. 检查 choices 增量（兼容 "choices":null / 非数组，避免 GetArrayLength 抛异常）
                if (root.TryGetProperty("choices", out var choicesElem) &&
                    choicesElem.ValueKind == JsonValueKind.Array &&
                    choicesElem.GetArrayLength() > 0)
                {
                    var choice = choicesElem[0];
                    if (choice.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (choice.TryGetProperty("delta", out var deltaElem) &&
                        deltaElem.ValueKind == JsonValueKind.Object)
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
                        if (deltaElem.TryGetProperty("tool_calls", out var toolCallsElem) &&
                            toolCallsElem.ValueKind == JsonValueKind.Array)
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

                                if (tcElem.TryGetProperty("function", out var funcElem) &&
                                    funcElem.ValueKind == JsonValueKind.Object)
                                {
                                    if (funcElem.TryGetProperty("name", out var nameElem))
                                    {
                                        string? name = nameElem.GetString();
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            state.FunctionName = name;
                                        }
                                    }

                                    if (!state.StartedEventEmitted &&
                                        !string.IsNullOrEmpty(state.CallId) &&
                                        !string.IsNullOrEmpty(state.FunctionName))
                                    {
                                        yield return UnifiedStreamEvent.ToolStarted(state.CallId, state.FunctionName);
                                        state.StartedEventEmitted = true;
                                    }

                                    if (funcElem.TryGetProperty("arguments", out var argsElem))
                                    {
                                        string? argsChunk = argsElem.GetString();
                                        if (!string.IsNullOrEmpty(argsChunk))
                                        {
                                            state.ArgumentsBuilder.Append(argsChunk);
                                            yield return UnifiedStreamEvent.ToolArgs(state.CallId, argsChunk);
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
                        if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var state in toolCallStates.Values)
                            {
                                if (state.StartedEventEmitted && !state.CompletedEventEmitted)
                                {
                                    yield return UnifiedStreamEvent.ToolDone(state.CallId, state.FunctionName, state.ArgumentsBuilder.ToString());
                                    state.CompletedEventEmitted = true;
                                }
                            }
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
                yield return UnifiedStreamEvent.ToolDone(state.CallId, state.FunctionName, state.ArgumentsBuilder.ToString());
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
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var errorElem) &&
                errorElem.ValueKind == JsonValueKind.Object)
            {
                string msg = errorElem.TryGetProperty("message", out var m) ? m.GetString() ?? raw : raw;
                string type = errorElem.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                string code = errorElem.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;

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
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var elem))
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
