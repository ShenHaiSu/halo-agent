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
    }

    /// <summary>测试用：注入 HttpClient（FakeHttpHandler）。</summary>
    public OpenAiCompatibleClient(LlmConfig cfg, HttpClient http)
    {
        _cfg = cfg;
        _http = http;
    }

    public string Model => _cfg.Model;
    public string? ProxyUrl => _cfg.ProxyUrl;

    // ============================================================
    //  配置
    // ============================================================

    public void Reconfigure(LlmConfig cfg)
    {
        // 代理换了必须重建 client（连接池绑在旧代理上）
        if (!string.Equals(cfg.ProxyUrl, _cfg.ProxyUrl, StringComparison.Ordinal))
        {
            _http.Dispose();
            _http = BuildHttp(cfg.ProxyUrl);
        }
        _cfg = cfg;
    }

    private static HttpClient BuildHttp(string? proxyUrl) =>
        // 模型可能很久才吐第一个 token：总超时 600s
        HttpClientFactory.Create(proxyUrl, timeoutSeconds: 600);

    private string ChatUrl() => _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
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

    private object BuildBody(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _cfg.Model,
            ["messages"] = messages,
            ["user"] = ReqUser,
            ["stream"] = stream,
        };
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
    //  辅助接口
    // ============================================================

    public async Task ValidateAsync(CancellationToken ct)
    {
        // 最短请求：只发一个 "hi"。有内容或无内容但请求成功都算可用
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
