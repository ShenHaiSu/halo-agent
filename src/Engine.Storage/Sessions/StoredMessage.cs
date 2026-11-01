using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 存储层自包含消息 DTO（与 OpenAI 兼容格式同线序）。
///
/// Storage 不引用 Engine.Core（铁律：Storage 不认识 agent），所以这里自带消息类型；
/// 与 Core 的 <c>ChatMessage</c> 互转由 Core 侧的 MessageAdapters 做。
/// P10：同步加 content_parts（读旧文件 parts 为空即回落纯文本）。
/// </summary>
public enum StoredRole { System, User, Assistant, Tool }

public sealed record StoredContentPart
{
    [JsonPropertyName("type")] public string Type { get; init; } = "text";
    [JsonPropertyName("text")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonPropertyName("image_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredImageUrl? ImageUrl { get; init; }
}

public sealed record StoredImageUrl
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public sealed record StoredMessage
{
    [JsonPropertyName("role")]
    public string RoleWire { get; init; } = "user";

    [JsonPropertyName("content")]
    public JsonElement ContentRaw { get; init; }

    [JsonIgnore]
    public string? Content
    {
        get
        {
            if (ContentRaw.ValueKind == JsonValueKind.String) return ContentRaw.GetString();
            return null;
        }
    }

    [JsonIgnore]
    public List<StoredContentPart>? ContentParts
    {
        get
        {
            if (ContentRaw.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<StoredContentPart>>(ContentRaw.GetRawText());
                }
                catch { return null; }
            }
            return null;
        }
    }

    public StoredMessage WithContent(string? content)
    {
        var el = content is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonDocument.Parse(JsonSerializer.Serialize(content)).RootElement.Clone();
        return this with { ContentRaw = el };
    }

    public StoredMessage WithParts(List<StoredContentPart> parts)
    {
        var el = JsonDocument.Parse(JsonSerializer.Serialize(parts)).RootElement.Clone();
        return this with { ContentRaw = el };
    }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StoredToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }

    /// <summary>P14-D1：UTC ms 时间戳，SSOT。0 = 未知（旧数据）。加性可选字段，不升 HistorySchema。</summary>
    [JsonPropertyName("ts_ms")] public long TsMs { get; init; }

    [JsonPropertyName("marker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Marker { get; init; }

    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Seq { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("removedTurns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RemovedTurns { get; init; }

    [JsonPropertyName("keptTurns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int KeptTurns { get; init; }

    [JsonIgnore]
    public bool IsCompactMarker => string.Equals(Marker, "compact", StringComparison.OrdinalIgnoreCase);

    public static StoredMessage System(string content, long tsMs = 0) =>
        FromContent("system", content, null, null, null, tsMs);

    public static StoredMessage User(string content, long tsMs = 0) =>
        FromContent("user", content, null, null, null, tsMs);

    public static StoredMessage Assistant(string content, long tsMs = 0) =>
        FromContent("assistant", content, null, null, null, tsMs);

    public static StoredMessage AssistantWithToolCalls(string? content, List<StoredToolCall> calls, long tsMs = 0) =>
        FromContent("assistant", content, calls, null, null, tsMs);

    public static StoredMessage Tool(string toolCallId, string content, long tsMs = 0) =>
        FromContent("tool", content, null, toolCallId, null, tsMs);

    private static StoredMessage FromContent(string role, string? content, List<StoredToolCall>? calls, string? toolCallId, string? reasoning, long tsMs = 0)
    {
        var el = content is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonDocument.Parse(JsonSerializer.Serialize(content)).RootElement.Clone();
        return new StoredMessage { RoleWire = role, ContentRaw = el, ToolCalls = calls, ToolCallId = toolCallId, ReasoningContent = reasoning, TsMs = tsMs };
    }

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
