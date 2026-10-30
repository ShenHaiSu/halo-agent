using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Net;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// OpenAI 兼容 /chat/completions 客户端。
///
/// ============ 容错清单（每条都有对应代码） ============
///   1. content 是字符串 → 直接用
///   2. content 是数组（[{type:"text",text:...}]）→ 拼接
///   3. content 为 null 但有 tool_calls → 正常
///   4. arguments 是字符串 → 直接用
///   5. arguments 是对象 → 序列化回字符串
///   6. tool_calls[].id 缺失 → 生成 call_&lt;name&gt;
///   7. 流式 tool_calls 按 index 累积
///   8. SSE 跨 chunk 缓冲（见 SseParser）
///   9. [DONE] 停止
///  10. finish_reason=length → 抛"输出被截断"
///  11. usage 在流末尾（stream_options.include_usage）
///  12. reasoning_content（DeepSeek/Qwen 思考模式）
///  13. 无法解析的行跳过，不中断流
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient, IDisposable
{
    private LlmConfig _cfg;
    private HttpClient _http;

    /// <summary>请求顶层 user 字段（遥测/实例区分用，固定值即可）。</summary>
    private const string ReqUser = "haloagent";

    public OpenAiCompatibleClient(LlmConfig cfg)
        : this(cfg, BuildHttp(cfg.ProxyUrl))
    {
        EnsureProtocol(cfg);
    }

    /// <summary>测试用：注入 HttpClient（FakeHttpHandler）。</summary>
    public OpenAiCompatibleClient(LlmConfig cfg, HttpClient http)
    {
        EnsureProtocol(cfg);
        _cfg = cfg;
        _http = http;
    }


    public string Model => _cfg.Model;
    public string? ProxyUrl => _cfg.ProxyUrl;

    // ============================================================
    public void Reconfigure(LlmConfig cfg)
    {
        // 两协议均允许（D6 一次性放开）
        if (cfg.Protocol != LlmProtocol.Chat && cfg.Protocol != LlmProtocol.Responses)
            throw new NotSupportedException("未知协议: " + cfg.Protocol);
        if (!string.Equals(cfg.ProxyUrl, _cfg.ProxyUrl, StringComparison.Ordinal))
        {
            _http.Dispose();
            _http = BuildHttp(cfg.ProxyUrl);
        }
        _cfg = cfg;
    }
    private static void EnsureChat(LlmConfig cfg)
    {
        if (cfg.Protocol != LlmProtocol.Chat)
            throw new NotSupportedException("当前为 Responses 协议，请调用 Responses* 方法。");
    }
    private static void EnsureResponses(LlmConfig cfg)
    {
        if (cfg.Protocol != LlmProtocol.Responses)
            throw new NotSupportedException("当前为 Chat 协议，请调用 Chat* 方法。");
    }
    private static void EnsureProtocol(LlmConfig cfg)
    {
        if (cfg.Protocol != LlmProtocol.Chat && cfg.Protocol != LlmProtocol.Responses)
            throw new NotSupportedException("未知协议: " + cfg.Protocol);
    }


    private static HttpClient BuildHttp(string? proxyUrl) =>
        // 模型可能很久才吐第一个 token：总超时 600s
        HttpClientFactory.Create(proxyUrl, timeoutSeconds: 600);

    private string ChatUrl() => _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
    private string ResponsesUrl() => _cfg.BaseUrl.TrimEnd('/') + "/responses";
    private string ModelsUrl() => _cfg.BaseUrl.TrimEnd('/') + "/models";

    private HttpRequestMessage Prepare(HttpMethod method, string url, object? body = null, string? sessionId = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_cfg.ApiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey);

        // 会话模板头：请求时展开；无会话（Validate/ListModels）时含模板的头跳过。保留头已在校验层剔除。
        foreach (var (k, v) in HeaderTemplate.ExpandAll(_cfg.Headers, sessionId))
            req.Headers.TryAddWithoutValidation(k, v);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }

    // ============================================================
    //  非流式
    // ============================================================

    public async Task<AssistantReply> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        CancellationToken ct,
        string? sessionId = null)
    {
        EnsureChat(_cfg);
        using var req = Prepare(HttpMethod.Post, ChatUrl(),
            BuildBody(messages, tools, stream: false), sessionId);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new EndpointStatusException((int)resp.StatusCode, text);

        return ParseChatResponse(text);
    }

    // ============================================================
    //  流式
    // ============================================================

    public async Task<AssistantReply> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        Action<StreamEvent> onEvent,
        CancellationToken ct,
        string? sessionId = null)
    {
        EnsureChat(_cfg);
        using var req = Prepare(HttpMethod.Post, ChatUrl(),
            BuildBody(messages, tools, stream: true), sessionId);
        using var resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new EndpointStatusException((int)resp.StatusCode, body);
        }

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var usage = new Usage();
        var acc = new ToolCallAccumulator();
        var sse = new SseParser();

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var chunk = new byte[8192];

        bool done = false;
        while (!done)
        {
            int n = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
            {
                foreach (var line in sse.Finish())
                {
                    if (ApplyStreamLine(line, content, reasoning, ref usage, acc, onEvent))
                    {
                        done = true;
                        break;
                    }
                }
                break;
            }

            foreach (var line in sse.Push(chunk, n))
            {
                if (ApplyStreamLine(line, content, reasoning, ref usage, acc, onEvent))
                {
                    done = true;
                    break;
                }
            }
        }

        return new AssistantReply
        {
            Content = content.Length == 0 ? null : content.ToString(),
            ReasoningContent = reasoning.Length == 0 ? null : reasoning.ToString(),
            ToolCalls = acc.Finish(),
            Usage = usage,
        };
    }

    /// <summary>处理一条 SSE data 负载。返回 true 表示遇到 [DONE]。</summary>
    private static bool ApplyStreamLine(
        string line,
        StringBuilder content,
        StringBuilder reasoning,
        ref Usage usage,
        ToolCallAccumulator acc,
        Action<StreamEvent> onEvent)
    {
        if (line == "[DONE]") return true;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return false; }   // 跳过坏行，不中断流

        using (doc)
        {
            var root = doc.RootElement;

            // usage（include_usage 时流末尾的一条）
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var p = u.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0;
                var c = u.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt64() : 0;
                if (p + c > 0) usage = new Usage(p, c);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var cEl) &&
                        cEl.ValueKind == JsonValueKind.String)
                    {
                        var s = cEl.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            content.Append(s);
                            onEvent(new StreamEvent.TextDelta(s));
                        }
                    }
                    if (delta.TryGetProperty("reasoning_content", out var rEl) &&
                        rEl.ValueKind == JsonValueKind.String)
                    {
                        var s = rEl.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            reasoning.Append(s);
                            onEvent(new StreamEvent.ReasoningDelta(s));
                        }
                    }
                    if (delta.TryGetProperty("tool_calls", out var tcEl) &&
                        tcEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcEl.EnumerateArray())
                        {
                            acc.Merge(tc);
                            onEvent(new StreamEvent.ToolCallDelta(
                                GetIndex(tc),
                                GetFunctionName(tc),
                                GetArgumentsFragment(tc)));
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind == JsonValueKind.String &&
                    fr.GetString() == "length")
                {
                    throw new InvalidOperationException("模型输出超过上下文长度被截断(length)");
                }
            }
        }
        return false;
    }

    private static int GetIndex(JsonElement tc) =>
        tc.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
            ? (int)idx.GetInt64() : 0;

    private static string GetFunctionName(JsonElement tc) =>
        tc.TryGetProperty("function", out var fn) &&
        fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? "" : "";

    private static string GetArgumentsFragment(JsonElement tc)
    {
        if (!tc.TryGetProperty("function", out var fn)) return "";
        if (!fn.TryGetProperty("arguments", out var a)) return "";
        return a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText();
    }

    // ============================================================
    //  请求体
    // ============================================================

    public object BuildBody(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, bool stream)
    {
        EnsureChat(_cfg);
        if (stream && _cfg.Protocol != LlmProtocol.Chat) throw new NotSupportedException("协议不匹配");
        var outgoing = MessageAdapters.StripReasoning(messages, _cfg.IncludeReasoningHistory);
        // 图片强校验（false 硬拦，不静默丢图）
        foreach (var m in outgoing) MessageAdapters.ValidateImages(m, _cfg.AcceptsImages);
        var body = new Dictionary<string, object?>
        {
            ["model"] = _cfg.Model,
            ["messages"] = outgoing,
            ["user"] = ReqUser,
            ["stream"] = stream,
        };
        // maxTokens 直通 max_tokens（只发该字段）
        if (_cfg.MaxTokens is not null) body["max_tokens"] = _cfg.MaxTokens.Value;
        if (stream)
        {
            // 让端点把真实用量放在流末尾的 usage 事件里
            body["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }
        if (tools is { Count: > 0 }) body["tools"] = tools;
        return body;
    }


    // ============================================================
    //  响应解析（容错）
    // ============================================================

    internal static AssistantReply ParseChatResponse(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"解析模型响应失败: {ex.Message}\n原始内容: {Truncate(text, 500)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("模型响应缺少 choices");

            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("模型响应 choices 为空");

            bool truncated = first.TryGetProperty("finish_reason", out var fr) &&
                             fr.ValueKind == JsonValueKind.String &&
                             fr.GetString() == "length";

            if (!first.TryGetProperty("message", out var msg))
                throw new InvalidOperationException("模型响应缺少 choices[0].message");

            var calls = new List<ToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var call = ValueToToolCall(tc);
                    if (call is not null) calls.Add(call);
                }
            }

            return new AssistantReply
            {
                Content = ExtractContentText(msg),
                ReasoningContent = msg.TryGetProperty("reasoning_content", out var r) &&
                                    r.ValueKind == JsonValueKind.String
                    ? r.GetString()
                    : null,
                ToolCalls = calls,
                Usage = ExtractUsage(root),
                TruncatedByLength = truncated,
            };
        }
    }

    /// <summary>content 容错：字符串或 [{type:"text",text:...}] 数组。</summary>
    private static string? ExtractContentText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String)
        {
            var s = content.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                sb.Append(item.GetString());
            }
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty("text", out var t) &&
                     t.ValueKind == JsonValueKind.String)
            {
                sb.Append(t.GetString());
            }
        }
        return sb.Length == 0 || string.IsNullOrWhiteSpace(sb.ToString()) ? null : sb.ToString();
    }

    private static Usage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return new Usage();

        long Get(string name) =>
            u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64() : 0;

        return new Usage(Get("prompt_tokens"), Get("completion_tokens"));
    }

    private static ToolCall? ValueToToolCall(JsonElement v)
    {
        if (!v.TryGetProperty("function", out var fn)) return null;
        if (!fn.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            return null;
        var name = n.GetString();
        if (string.IsNullOrEmpty(name)) return null;

        // arguments 容错：字符串直接用；对象序列化；缺失给 {}
        string args;
        if (!fn.TryGetProperty("arguments", out var a) || a.ValueKind == JsonValueKind.Null)
            args = "{}";
        else if (a.ValueKind == JsonValueKind.String)
            args = a.GetString() ?? "{}";
        else
            args = a.GetRawText();

        var id = v.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrEmpty(idEl.GetString())
            ? idEl.GetString()!
            : "call_" + name;

        var type = v.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrEmpty(ty.GetString())
            ? ty.GetString()!
            : "function";

        return new ToolCall
        {
            Id = id,
            Type = type,
            Function = new FunctionCall { Name = name, Arguments = args },
        };
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    // ============================================================
    //  Responses 协议
    // ============================================================
    public object BuildResponsesBody(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, bool stream)
    {
        EnsureResponses(_cfg);
        if (instructions is not null && input.Count > 0)
        {
            foreach (var it in input)
            {
                try
                {
                    var j = System.Text.Json.JsonSerializer.Serialize(it);
                    using var d = System.Text.Json.JsonDocument.Parse(j);
                    if (d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && d.RootElement.TryGetProperty("role", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String && string.Equals(r.GetString(), "system", System.StringComparison.OrdinalIgnoreCase))
                        throw new System.InvalidOperationException("system 不得同时进 instructions 与 input");
                }
                catch (System.InvalidOperationException) { throw; }
                catch { }
            }
        }
        var body = new System.Collections.Generic.Dictionary<string, object?>();
        body["model"] = _cfg.Model;
        if (previousResponseId is not null) body["previous_response_id"] = previousResponseId;
        if (instructions is not null) body["instructions"] = instructions;
        body["input"] = input;
        if (tools is { Count: > 0 }) body["tools"] = tools;
        body["stream"] = stream;
        if (_cfg.MaxTokens is not null) body["max_output_tokens"] = _cfg.MaxTokens.Value;
        body["user"] = ReqUser;
        body["store"] = false;
        return body;
    }
    public async Task<ResponsesReply> ResponsesAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, CancellationToken ct, string? sessionId = null)
    {
        EnsureResponses(_cfg);
        using var req = Prepare(HttpMethod.Post, ResponsesUrl(), BuildResponsesBody(previousResponseId, instructions, input, tools, stream: false), sessionId);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new EndpointStatusException((int)resp.StatusCode, text);
        return ParseResponsesResponse(text);
    }
    public async Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
    {
        EnsureResponses(_cfg);
        using var req = Prepare(HttpMethod.Post, ResponsesUrl(), BuildResponsesBody(previousResponseId, instructions, input, tools, stream: true), sessionId);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { var b = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); throw new EndpointStatusException((int)resp.StatusCode, b); }
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var usage = new Usage();
        var acc = new FunctionCallAccumulator();
        var unknowns = new List<UnknownOutput>();
        string? responseId = null;
        string? incompleteReason = null;
        bool truncated = false;
        var sse = new SseParser();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var chunk = new byte[8192];
        bool done = false;
        while (!done)
        {
            int n = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) { foreach (var line in sse.Finish()) { if (ApplyResponsesLine(line, content, reasoning, ref usage, acc, unknowns, ref responseId, ref incompleteReason, ref truncated, onEvent)) { done = true; break; } } break; }
            foreach (var line in sse.Push(chunk, n)) { if (ApplyResponsesLine(line, content, reasoning, ref usage, acc, unknowns, ref responseId, ref incompleteReason, ref truncated, onEvent)) { done = true; break; } }
        }
        if (incompleteReason == "max_output_tokens" || truncated) throw new InvalidOperationException("模型输出超过上下文长度被截断(max_output_tokens)");
        if (incompleteReason is not null && incompleteReason != "completed") throw new InvalidOperationException("Responses incomplete: " + incompleteReason);
        return new ResponsesReply { ResponseId = responseId, Content = content.Length == 0 ? null : content.ToString(), ReasoningContent = reasoning.Length == 0 ? null : reasoning.ToString(), ReasoningKind = "summary", ToolCalls = acc.Finish(), UnknownOutputs = unknowns, Usage = usage, TruncatedByLength = truncated, IncompleteReason = incompleteReason };
    }
    private static bool ApplyResponsesLine(string line, StringBuilder content, StringBuilder reasoning, ref Usage usage, FunctionCallAccumulator acc, List<UnknownOutput> unknowns, ref string? responseId, ref string? incompleteReason, ref bool truncated, Action<StreamEvent> onEvent)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return false; }
        using (doc)
        {
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type == "response.output_text.delta")
            {
                var s = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!string.IsNullOrEmpty(s)) { content.Append(s); onEvent(new StreamEvent.TextDelta(s)); }
                return false;
            }
            if (type == "response.reasoning_summary_text.delta")
            {
                var s = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!string.IsNullOrEmpty(s)) { reasoning.Append(s); onEvent(new StreamEvent.ReasoningDelta(s)); }
                return false;
            }
            if (type == "response.function_call_arguments.delta")
            {
                string? oi = root.TryGetProperty("output_index", out var o) ? o.ToString() : null;
                string? iid = root.TryGetProperty("item_id", out var ii) && ii.ValueKind == JsonValueKind.String ? ii.GetString() : null;
                string? cid = root.TryGetProperty("call_id", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString() : null;
                string? nm = root.TryGetProperty("name", out var nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() : null;
                string? frag = root.TryGetProperty("delta", out var dd) ? (dd.ValueKind == JsonValueKind.String ? dd.GetString() : dd.GetRawText()) : null;
                if (string.IsNullOrEmpty(nm) && root.TryGetProperty("function_call", out var fc) && fc.ValueKind == JsonValueKind.Object)
                {
                    if (fc.TryGetProperty("name", out var fn2) && fn2.ValueKind == JsonValueKind.String) nm = fn2.GetString();
                    if (fc.TryGetProperty("call_id", out var fc2) && fc2.ValueKind == JsonValueKind.String) cid ??= fc2.GetString();
                }
                acc.Merge(oi, iid, cid, nm, frag);
                onEvent(new StreamEvent.ToolCallDelta(0, nm ?? "", frag ?? ""));
                return false;
            }
            if (type == "response.completed" || type == "completed")
            {
                if (root.TryGetProperty("response", out var r2) && r2.ValueKind == JsonValueKind.Object)
                {
                    if (r2.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) responseId = idEl.GetString();
                    if (r2.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        long gi(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                        long p = gi("input_tokens"), c = gi("output_tokens");
                        if (p + c > 0) usage = new Usage(p, c);
                    }
                    if (r2.TryGetProperty("incomplete", out var inc) && inc.ValueKind == JsonValueKind.Object)
                    {
                        if (inc.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String) incompleteReason = rr.GetString();
                    }
                    if (r2.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "incomplete")
                    {
                        if (string.IsNullOrEmpty(incompleteReason)) incompleteReason = "incomplete";
                    }
                }
                return true;
            }
            if (type == "response.incomplete")
            {
                if (root.TryGetProperty("response", out var r3) && r3.ValueKind == JsonValueKind.Object)
                {
                    if (r3.TryGetProperty("id", out var idEl2) && idEl2.ValueKind == JsonValueKind.String) responseId = idEl2.GetString();
                    if (r3.TryGetProperty("incomplete", out var inc2) && inc2.ValueKind == JsonValueKind.Object && inc2.TryGetProperty("reason", out var rr2) && rr2.ValueKind == JsonValueKind.String) incompleteReason = rr2.GetString();
                    else incompleteReason = "incomplete";
                }
                else incompleteReason = "incomplete";
                truncated = string.Equals(incompleteReason, "max_output_tokens", StringComparison.Ordinal);
                return true;
            }
            if (type == "response.failed" || type == "failed")
            {
                string body = root.GetRawText();
                int st2 = 500;
                if (root.TryGetProperty("response", out var rf) && rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number) st2 = (int)sc.GetInt64();
                throw new EndpointStatusException(st2, body);
            }
            if (root.TryGetProperty("response", out var rr3) && rr3.ValueKind == JsonValueKind.Object && rr3.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(responseId)) responseId = rid.GetString();
            return false;
        }
    }
    internal static ResponsesReply ParseResponsesResponse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object) { if (r.TryGetProperty("id", out var id2) && id2.ValueKind == JsonValueKind.String) id ??= id2.GetString(); }
        var sb = new StringBuilder(); var rsb = new StringBuilder();
        var calls = new List<ToolCall>(); var unknowns = new List<UnknownOutput>();
        Usage usage = new();
        bool trunc = false; string? incReason = null;
        if (root.TryGetProperty("usage", out var u2) && u2.ValueKind == JsonValueKind.Object)
        {
            long g(string n) => u2.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            usage = new Usage(g("input_tokens"), g("output_tokens"));
        }
        else if (root.TryGetProperty("response", out var rr4) && rr4.ValueKind == JsonValueKind.Object && rr4.TryGetProperty("usage", out var u3) && u3.ValueKind == JsonValueKind.Object)
        {
            long g2(string n) => u3.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            usage = new Usage(g2("input_tokens"), g2("output_tokens"));
        }
        JsonElement output = default;
        bool hasOutput = false;
        if (root.TryGetProperty("output", out var o2) && o2.ValueKind == JsonValueKind.Array) { output = o2; hasOutput = true; }
        else if (root.TryGetProperty("response", out var r5) && r5.ValueKind == JsonValueKind.Object && r5.TryGetProperty("output", out var o3) && o3.ValueKind == JsonValueKind.Array) { output = o3; hasOutput = true; }
        if (hasOutput)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? ot = item.TryGetProperty("type", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString() : null;
                if (ot == "message" || ot == "output_text")
                {
                    if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                        foreach (var p in c.EnumerateArray()) { if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) sb.Append(tx.GetString()); }
                    else if (item.TryGetProperty("text", out var tx2) && tx2.ValueKind == JsonValueKind.String) sb.Append(tx2.GetString());
                }
                else if (ot == "reasoning") { if (item.TryGetProperty("summary", out var s2) && s2.ValueKind == JsonValueKind.Array) foreach (var p in s2.EnumerateArray()) { if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var tx3) && tx3.ValueKind == JsonValueKind.String) rsb.Append(tx3.GetString()); } }
                else if (ot == "function_call")
                {
                    string? nm = item.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
                    string? cid = item.TryGetProperty("call_id", out var ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString() : (item.TryGetProperty("id", out var ie2) && ie2.ValueKind == JsonValueKind.String ? ie2.GetString() : null);
                    string args = item.TryGetProperty("arguments", out var ae) ? (ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "{}" : ae.GetRawText()) : "{}";
                    if (!string.IsNullOrEmpty(nm)) calls.Add(new ToolCall { Id = string.IsNullOrEmpty(cid) ? "call_" + nm : cid!, Type = "function", Function = new FunctionCall { Name = nm!, Arguments = args } });
                    else unknowns.Add(new UnknownOutput(ot ?? "unknown", item.Clone()));
                }
                else if (!string.IsNullOrEmpty(ot)) unknowns.Add(new UnknownOutput(ot!, item.Clone()));
            }
        }
        if (root.TryGetProperty("incomplete", out var incEl) && incEl.ValueKind == JsonValueKind.Object && incEl.TryGetProperty("reason", out var ir) && ir.ValueKind == JsonValueKind.String) { incReason = ir.GetString(); trunc = string.Equals(incReason, "max_output_tokens", StringComparison.Ordinal); }
        return new ResponsesReply { ResponseId = id, Content = sb.Length == 0 ? null : sb.ToString(), ReasoningContent = rsb.Length == 0 ? null : rsb.ToString(), ReasoningKind = "summary", ToolCalls = calls, UnknownOutputs = unknowns, Usage = usage, TruncatedByLength = trunc, IncompleteReason = incReason };
    }

    // ============================================================
    //  辅助接口
    // ============================================================

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (_cfg.Protocol == LlmProtocol.Responses)
        {
            var probe = BuildResponsesBody(null, null, new object[] { new Dictionary<string, object?> { ["type"] = "message", ["role"] = "user", ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "ping" } } } }, null, stream: false);
            using var req2 = Prepare(HttpMethod.Post, ResponsesUrl(), probe);
            using var resp2 = await _http.SendAsync(req2, ct).ConfigureAwait(false);
            var t2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp2.IsSuccessStatusCode) throw new EndpointStatusException((int)resp2.StatusCode, t2);
            return;
        }
        var messages = new[] { ChatMessage.User("hi") };
        await ChatAsync(messages, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = Prepare(HttpMethod.Get, ModelsUrl());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new EndpointStatusException((int)resp.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var models = new List<string>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var s = id.GetString();
                if (!string.IsNullOrEmpty(s)) models.Add(s);
            }
        }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    public void Dispose() => _http.Dispose();
}
