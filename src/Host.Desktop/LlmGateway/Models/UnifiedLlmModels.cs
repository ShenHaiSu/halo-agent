using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;

namespace Host.Desktop.LlmGateway.Models;

/// <summary>
/// 统一大模型请求上下文（完全解耦底层 Chat 与 Responses 协议）
/// </summary>
public sealed class UnifiedLlmRequest
{
    public string RequestId { get; init; } = string.Empty;
    public ProviderConfig Provider { get; init; } = new();
    public ModelConfig Model { get; init; } = new();
    public HeaderResolutionContext HeaderContext { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    public string SystemInstructions { get; init; } = string.Empty;
    public IReadOnlyList<UnifiedChatMessage> Messages { get; init; } = Array.Empty<UnifiedChatMessage>();
    public IReadOnlyList<UnifiedToolDefinition> Tools { get; init; } = Array.Empty<UnifiedToolDefinition>();
}

/// <summary>
/// 统一消息体（支持 user / assistant / tool / system）
/// </summary>
public sealed class UnifiedChatMessage
{
    public string Role { get; init; } = "user"; // "system" | "user" | "assistant" | "tool"
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public IReadOnlyList<UnifiedToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; } // 仅当 Role == "tool" 时有效，记录所响应的 call_id

    public static UnifiedChatMessage System(string content) =>
        new() { Role = "system", Content = content };

    public static UnifiedChatMessage User(string content) =>
        new() { Role = "user", Content = content };

    public static UnifiedChatMessage Assistant(
        string? content,
        IReadOnlyList<UnifiedToolCall>? toolCalls = null,
        string? reasoningContent = null) =>
        new()
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls,
            ReasoningContent = reasoningContent
        };

    public static UnifiedChatMessage Tool(string toolCallId, string output) =>
        new()
        {
            Role = "tool",
            ToolCallId = toolCallId,
            Content = output
        };
}

/// <summary>
/// 统一工具描述符
/// </summary>
public sealed class UnifiedToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>
    /// 标准 JSON Schema 对象（type: "object", properties: {...}, required: [...]）
    /// </summary>
    public JsonElement ParametersSchema { get; init; }

    public static UnifiedToolDefinition Create(string name, string description, string jsonSchema)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonSchema) ? "{}" : jsonSchema);
        return new UnifiedToolDefinition
        {
            Name = name,
            Description = description,
            ParametersSchema = doc.RootElement.Clone()
        };
    }
}

/// <summary>
/// 统一流式事件类型枚举
/// </summary>
public enum UnifiedStreamEventKind
{
    StreamStarted,       // 流已建立
    ReasoningDelta,      // 思考过程增量（兼容 DeepSeek reasoning_content / OpenAI reasoning summary）
    TextDelta,           // 正文文本增量
    ToolCallStarted,     // 模型决定发起某个工具调用（已拿到 callId 与 functionName）
    ToolCallArgsDelta,   // 工具参数 JSON 增量片段
    ToolCallCompleted,   // 单个工具调用参数接收完毕
    UsageUpdated,        // Token 消耗统计（含 Prompt Cache 命中统计）
    StreamCompleted,     // 整轮响应完成
    StreamCancelled,     // 用户主动取消中途生成
    StreamError          // 协议层或网络层错误
}

/// <summary>
/// 统一流式事件载荷
/// </summary>
public sealed record UnifiedStreamEvent(
    UnifiedStreamEventKind Kind,
    string? TextDelta = null,
    UnifiedToolCall? ToolCall = null,
    UnifiedTokenUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null
)
{
    public static UnifiedStreamEvent Started() =>
        new(UnifiedStreamEventKind.StreamStarted);

    public static UnifiedStreamEvent Text(string delta) =>
        new(UnifiedStreamEventKind.TextDelta, TextDelta: delta);

    public static UnifiedStreamEvent Reasoning(string delta) =>
        new(UnifiedStreamEventKind.ReasoningDelta, TextDelta: delta);

    public static UnifiedStreamEvent ToolStarted(string callId, string functionName, string? itemId = null) =>
        new(UnifiedStreamEventKind.ToolCallStarted,
            ToolCall: new UnifiedToolCall(callId, itemId, functionName, string.Empty));

    public static UnifiedStreamEvent ToolArgs(string callId, string argsDelta, string? itemId = null) =>
        new(UnifiedStreamEventKind.ToolCallArgsDelta,
            TextDelta: argsDelta,
            ToolCall: new UnifiedToolCall(callId, itemId, string.Empty, argsDelta));

    public static UnifiedStreamEvent ToolDone(string callId, string functionName, string argumentsJson, string? itemId = null) =>
        new(UnifiedStreamEventKind.ToolCallCompleted,
            ToolCall: new UnifiedToolCall(callId, itemId, functionName, argumentsJson));

    public static UnifiedStreamEvent Tokens(int input, int output, int cached, int total) =>
        new(UnifiedStreamEventKind.UsageUpdated,
            Usage: new UnifiedTokenUsage(input, output, cached, total));

    public static UnifiedStreamEvent Completed() =>
        new(UnifiedStreamEventKind.StreamCompleted);

    public static UnifiedStreamEvent Cancelled() =>
        new(UnifiedStreamEventKind.StreamCancelled);

    public static UnifiedStreamEvent Error(string code, string message) =>
        new(UnifiedStreamEventKind.StreamError,
            ErrorCode: code,
            ErrorMessage: message);
}

/// <summary>
/// 统一工具调用数据
/// </summary>
public sealed record UnifiedToolCall(
    string CallId,          // 对应 Chat 的 tool_call.id 或 Responses 的 call_id
    string? ItemId,         // 对应 Responses API 的 fc_xxx item id（可选）
    string FunctionName,
    string ArgumentsJson
);

/// <summary>
/// 统一 Token 用量与 Prompt Cache 命中指标
/// </summary>
public sealed record UnifiedTokenUsage(
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,  // 重点：记录 Prompt Cache 命中的 Token 数，验证 ${session-hash} 收益
    int TotalTokens
)
{
    public double CacheHitRate => InputTokens > 0
        ? Math.Clamp((double)CachedInputTokens / InputTokens, 0.0, 1.0)
        : 0.0;
}

/// <summary>
/// 投递至前端的会话流式事件 DTO，支持 16ms 节流消费周期的增量文本合并优化 (Delta Coalescing)
/// </summary>
public sealed class SessionStreamEventDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? TextDelta { get; set; }
    public UnifiedToolCall? ToolCall { get; set; }
    public UnifiedTokenUsage? Usage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static SessionStreamEventDto FromUnifiedEvent(string sessionId, UnifiedStreamEvent evt) =>
        new()
        {
            SessionId = sessionId,
            Kind = evt.Kind.ToString(),
            TextDelta = evt.TextDelta,
            ToolCall = evt.ToolCall,
            Usage = evt.Usage,
            ErrorCode = evt.ErrorCode,
            ErrorMessage = evt.ErrorMessage
        };
}
