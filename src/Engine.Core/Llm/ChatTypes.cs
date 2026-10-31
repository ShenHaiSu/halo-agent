using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Core.Llm;

public enum Role { System, User, Assistant, Tool }

public enum LlmProtocol { Chat, Responses }

/// <summary>图片/文本内容块（P10：ChatMessage 发送格式升级）。</summary>
public sealed record ChatContentPart
{
    [JsonPropertyName("type")] public string Type { get; init; } = "text"; // text | image_url
    [JsonPropertyName("text")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonPropertyName("image_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatImageUrl? ImageUrl { get; init; }

    public static ChatContentPart TextPart(string text) => new() { Type = "text", Text = text };
    public static ChatContentPart ImagePart(string url) => new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = url } };
}

public sealed record ChatImageUrl
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

/// <summary>
/// 对话消息（OpenAI 兼容格式）。
/// Content 纯文本快车道保留；有图时使用 ContentParts（序列化为 content 数组）。
/// </summary>
[JsonConverter(typeof(ChatMessageJsonConverter))]
public sealed record ChatMessage
{
    public string RoleWire { get; init; } = "user";
    public string? Content { get; init; }
    public List<ChatContentPart>? ContentParts { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ReasoningContent { get; init; }

    public static ChatMessage System(string content) =>
        new() { RoleWire = "system", Content = content };

    public static ChatMessage User(string content) =>
        new() { RoleWire = "user", Content = content };

    public static ChatMessage UserWithParts(List<ChatContentPart> parts) =>
        new() { RoleWire = "user", ContentParts = parts };

    public static ChatMessage Assistant(string content) =>
        new() { RoleWire = "assistant", Content = content };

    public static ChatMessage AssistantWithToolCalls(string? content, List<ToolCall> calls) =>
        new() { RoleWire = "assistant", Content = content, ToolCalls = calls };

    public static ChatMessage Tool(string toolCallId, string content) =>
        new() { RoleWire = "tool", Content = content, ToolCallId = toolCallId };

    [JsonIgnore]
    public Role Role => RoleWire switch
    {
        "system" => Role.System,
        "assistant" => Role.Assistant,
        "tool" => Role.Tool,
        _ => Role.User,
    };

    public bool HasImages => ContentParts is { Count: > 0 } &&
        ContentParts.Any(p => p.Type == "image_url" && p.ImageUrl is not null);
}

public sealed class ChatMessageJsonConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string role = root.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? "user" : "user";
        string? toolCallId = root.TryGetProperty("tool_call_id", out var tcid) && tcid.ValueKind == JsonValueKind.String
            ? tcid.GetString() : null;
        string? reasoning = root.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
            ? rc.GetString() : null;
        List<ToolCall>? calls = null;
        if (root.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            calls = JsonSerializer.Deserialize<List<ToolCall>>(tcs.GetRawText(), options);

        string? content = null;
        List<ChatContentPart>? parts = null;
        if (root.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String) content = c.GetString();
            else if (c.ValueKind == JsonValueKind.Array)
                parts = JsonSerializer.Deserialize<List<ChatContentPart>>(c.GetRawText(), options);
            else if (c.ValueKind == JsonValueKind.Null) content = null;
        }
        // parts 回落：若数组全是 text，可拼回 Content？保留 parts 原样即可
        return new ChatMessage
        {
            RoleWire = role, Content = content, ContentParts = parts,
            ToolCalls = calls, ToolCallId = toolCallId, ReasoningContent = reasoning,
        };
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.RoleWire);
        if (value.ContentParts is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.ContentParts, options);
        }
        else
        {
            if (value.Content is not null) writer.WriteString("content", value.Content);
            else writer.WriteNull("content");
        }
        if (value.ToolCalls is not null)
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        if (value.ToolCallId is not null) writer.WriteString("tool_call_id", value.ToolCallId);
        if (value.ReasoningContent is not null) writer.WriteString("reasoning_content", value.ReasoningContent);
        writer.WriteEndObject();
    }
}

public sealed record ToolCall
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public FunctionCall Function { get; init; } = new();
}

public sealed record FunctionCall
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>原始 JSON 字符串（可能被截断，由 ToolArgumentParser 修补）</summary>
    [JsonPropertyName("arguments")] public string Arguments { get; init; } = "";
}

/// <summary>流式增量。</summary>
public abstract record StreamEvent
{
    public sealed record TextDelta(string Text) : StreamEvent;
    public sealed record ReasoningDelta(string Text) : StreamEvent;
    public sealed record ToolCallDelta(int Index, string Name, string ArgumentsFragment) : StreamEvent;
}

public sealed record Usage(long PromptTokens = 0, long CompletionTokens = 0)
{
    public long Total => PromptTokens + CompletionTokens;
    public bool IsEmpty => Total == 0;
}

/// <summary>一次模型回复（流式累积后的完整结果）。</summary>
public sealed record AssistantReply
{
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public List<ToolCall> ToolCalls { get; init; } = new();
    public Usage Usage { get; init; } = new();
    /// <summary>finish_reason=length：输出被截断（非流式时标记，流式时直接抛）。</summary>
    public bool TruncatedByLength { get; init; }
}

/// <summary>端点返回非 2xx。带结构化状态码，供重试判定用。</summary>
public sealed class EndpointStatusException : Exception
{
    public int Status { get; }
    public string Body { get; }

    public EndpointStatusException(int status, string body)
        : base($"模型端点返回 {status}: {Truncate(body, 500)}")
    {
        Status = status;
        Body = body;
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}

public interface ILlmClient
{
    string Model { get; }
    string? ProxyUrl { get; }

    Task<AssistantReply> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        CancellationToken ct,
        string? sessionId = null);

    Task<AssistantReply> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        Action<StreamEvent> onEvent,
        CancellationToken ct,
        string? sessionId = null);
    Task<ResponsesReply> ResponsesAsync(
        string? previousResponseId,
        string? instructions,
        IReadOnlyList<object> input,
        IReadOnlyList<ResponsesToolDef>? tools,
        CancellationToken ct,
        string? sessionId = null);
    Task<ResponsesReply> ResponsesStreamAsync(
        string? previousResponseId,
        string? instructions,
        IReadOnlyList<object> input,
        IReadOnlyList<ResponsesToolDef>? tools,
        Action<StreamEvent> onEvent,
        CancellationToken ct,
        string? sessionId = null);
    object BuildResponsesBody(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, bool stream);
    Task ValidateAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
    void Reconfigure(LlmConfig cfg);
}

public sealed record LlmConfig
{
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string? ApiKey { get; init; }
    public string? ProxyUrl { get; init; }
    /// <summary>请求头模板原文（含 `{{session_id}}` / `{{session_hash}}`）；请求时由 HeaderTemplate 展开。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public LlmProtocol Protocol { get; init; } = LlmProtocol.Chat;
    public int? MaxTokens { get; init; }
    public bool AcceptsImages { get; init; }
     public bool IncludeReasoningHistory { get; init; } = true;
     /// <summary>P16-F2：responses 是否要求服务端持久化（store=true 才能多轮 previous_response_id 链）。</summary>
     public bool StoreResponses { get; init; } = true;
 }

/// <summary>发给模型的工具定义（OpenAI tools 格式）。</summary>
public sealed record ToolDef
{
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public FunctionDef Function { get; init; } = new();
}

public sealed record FunctionDef
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("parameters")] public object? Parameters { get; init; }
}
