using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Core.Llm;

public enum Role { System, User, Assistant, Tool }

/// <summary>
/// 对话消息（OpenAI 兼容格式）。
///
/// 注意：这是**给模型看的**格式，与 P1 契约里的 MessageView（渲染友好）不同。
/// 转换在 MessageProjector 里做。
/// </summary>
public sealed record ChatMessage
{
    [JsonPropertyName("role")]
    public string RoleWire { get; init; } = "user";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }

    public static ChatMessage System(string content) =>
        new() { RoleWire = "system", Content = content };

    public static ChatMessage User(string content) =>
        new() { RoleWire = "user", Content = content };

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
