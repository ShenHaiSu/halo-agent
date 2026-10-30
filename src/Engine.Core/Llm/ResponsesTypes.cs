using System.Text.Json;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>Responses 输出项（input 形态）。</summary>
public sealed record ResponseInputItem
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; init; } = "message";
    [System.Text.Json.Serialization.JsonPropertyName("role")] public string? Role { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("content")] public object? Content { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("call_id")] public string? CallId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("arguments")] public string? Arguments { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("output")] public string? Output { get; init; }
}

/// <summary>Responses 工具定义（扁平形）。</summary>
public sealed record ResponsesToolDef
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; init; } = "function";
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("description")] public string Description { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("parameters")] public object? Parameters { get; init; }
}

/// <summary>未知输出（内存态）。</summary>
public sealed record UnknownOutput(string RawType, JsonElement RawJson);

/// <summary>一次 Responses 回复。</summary>
public sealed record ResponsesReply
{
    public string? ResponseId { get; init; }
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public string ReasoningKind { get; init; } = "summary";
    public List<ToolCall> ToolCalls { get; init; } = new();
    public IReadOnlyList<UnknownOutput> UnknownOutputs { get; init; } = Array.Empty<UnknownOutput>();
    public Usage Usage { get; init; } = new();
    public bool TruncatedByLength { get; init; }
    public string? IncompleteReason { get; init; }
}
