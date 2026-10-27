using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 存储层自包含消息 DTO（与 OpenAI 兼容格式同线序）。
///
/// Storage 不引用 Engine.Core（铁律：Storage 不认识 agent），所以这里自带消息类型；
/// 与 Core 的 <c>ChatMessage</c> 互转由 Core 侧的 MessageAdapters 做。
/// </summary>
public enum StoredRole { System, User, Assistant, Tool }

public sealed record StoredMessage
{
    [JsonPropertyName("role")]
    public string RoleWire { get; init; } = "user";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StoredToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }

    public static StoredMessage System(string content) =>
        new() { RoleWire = "system", Content = content };

    public static StoredMessage User(string content) =>
        new() { RoleWire = "user", Content = content };

    public static StoredMessage Assistant(string content) =>
        new() { RoleWire = "assistant", Content = content };

    public static StoredMessage AssistantWithToolCalls(string? content, List<StoredToolCall> calls) =>
        new() { RoleWire = "assistant", Content = content, ToolCalls = calls };

    public static StoredMessage Tool(string toolCallId, string content) =>
        new() { RoleWire = "tool", Content = content, ToolCallId = toolCallId };

    [JsonIgnore]
    public StoredRole Role => RoleWire switch
    {
        "system" => StoredRole.System,
        "assistant" => StoredRole.Assistant,
        "tool" => StoredRole.Tool,
        _ => StoredRole.User,
    };
}

public sealed record StoredToolCall
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "function";
    [JsonPropertyName("function")] public StoredFunctionCall Function { get; init; } = new();
}

public sealed record StoredFunctionCall
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>原始 JSON 字符串（可能被截断，由 ToolArgumentParser 修补）。</summary>
    [JsonPropertyName("arguments")] public string Arguments { get; init; } = "";
}
