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
    /// <summary>need09-05：图片引用 → dataURL 解析器（默认 ImageDataRegistry；单测可注入假数据）。返回 null 表示该图无字节（发送时省略，文本保留）。</summary>
    [JsonIgnore]
    public Func<ChatImageRef, string?>? ImageResolver { get; init; }
}
/// <summary>need09-03/05：非视觉模型收到含图轮次时由 Adapter 抛出；Orchestrator 捕获后纯文本降级重发一次。</summary>
public sealed class NonVisionModelException : Exception
{
    public NonVisionModelException(string modelId, int imageCount)
        : base($"模型 '{modelId}' 不支持视觉输入，本轮含 {imageCount} 张图片，已拦截发送。")
    {
        ModelId = modelId ?? string.Empty;
        ImageCount = imageCount;
    }
    public string ModelId { get; }
    public int ImageCount { get; }
}
/// <summary>need09-05：图片字节内存注册表（生产由 SessionRepository 读盘回填；单测注入假 dataURL）。</summary>
public static class ImageDataRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);
    public static void Register(string imageId, string dataUrl)
    {
        if (!string.IsNullOrWhiteSpace(imageId) && !string.IsNullOrWhiteSpace(dataUrl)) _map[imageId.Trim()] = dataUrl.Trim();
    }
    public static string? Resolve(ChatImageRef? img)
    {
        if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
        return _map.TryGetValue(img.ImageId, out var url) ? url : null;
    }
    public static void Clear() => _map.Clear();
}
/// <summary>need09-05：adapter 共享的多模态轮次规划结果项。</summary>
public sealed record ResolvedImage(ChatImageRef Ref, string? DataUrl);
/// <summary>need09-05：adapter 共享的多模态轮次规划（门控加取最新 N 保 latest 丢最旧加dataURL 解析）。</summary>
public static class MultimodalPlanner
{
    /// <summary>规划本轮图片：非视觉模型含图抛 NonVisionModelException；超 Max 取最新 N 张。返回与 Messages 等长对齐的 kept 列表。</summary>
    public static (List<List<ResolvedImage>> Kept, int Dropped, int Total) PlanTurn(UnifiedLlmRequest request)
    {
        var kept = new List<List<ResolvedImage>>();
        int total = 0;
        foreach (var msg in request.Messages)
        {
            var list = new List<ResolvedImage>();
            if (msg != null && msg.Images != null)
            {
                bool carries = msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    || msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
                if (carries)
                {
                    foreach (var img in msg.Images)
                    {
                        if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) continue;
                        total++;
                        string? url = request.ImageResolver != null ? request.ImageResolver(img) : ImageDataRegistry.Resolve(img);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            url = url.Trim();
                            if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                string mime = string.IsNullOrWhiteSpace(img.Mime) ? "image/jpeg" : img.Mime.Trim();
                                url = "data:" + mime + ";base64," + url;
                            }
                        }
                        else { url = null; }
                        list.Add(new ResolvedImage(img, url));
                    }
                }
            }
            kept.Add(list);
        }
        if (total > 0 && !request.Model.SupportsVision) throw new NonVisionModelException(request.Model.Id, total);
        int max = VisionDefaults.ResolveMaxImages(request.Model.MaxImagesPerTurn);
        int dropped = 0;
        if (total > max)
        {
            dropped = total - max;
            int skip = dropped;
            for (int i = 0; i < kept.Count && skip > 0; i++)
            {
                while (kept[i].Count > 0 && skip > 0) { kept[i].RemoveAt(0); skip--; }
            }
        }
        return (kept, dropped, total);
    }
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
