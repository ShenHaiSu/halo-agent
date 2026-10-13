using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Core.Models;

/// <summary>
/// 工作区信息模型（对应多项目工作路径）
/// </summary>
public sealed class WorkspaceInfo
{
    /// <summary>
    /// 工作区唯一 ID (如 "ws_9a8b7c6d")
    /// </summary>
    public string Id { get; init; } = $"ws_{Guid.NewGuid():N}"[..11];

    /// <summary>
    /// 显示名称（默认取文件夹名，允许用户自定义别名）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 规范化后的本地绝对路径（如 "F:\MyProgram\CSharp\halo-agent"）
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// 路径确定性哈希（用于 ${workspace-hash} 预设变量）
    /// </summary>
    public string WorkspaceHash { get; set; } = string.Empty;

    /// <summary>
    /// 该项目上次选用的 ProviderId 与 ModelId
    /// </summary>
    public string? DefaultProviderId { get; set; }
    public string? DefaultModelId { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastOpenedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 运行时动态计算属性：当前磁盘上该文件夹是否存在
    /// </summary>
    [JsonIgnore]
    public bool DirectoryExists => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);
}

/// <summary>
/// 会话元数据模型
/// </summary>
public sealed class SessionMetadata
{
    /// <summary>
    /// 会话唯一 ID (如 "sess_20260330_a1b2c3d4")
    /// </summary>
    public string Id { get; init; } = $"sess_{DateTime.UtcNow:yyyyMMdd}_{Guid.NewGuid():N}"[..22];

    /// <summary>
    /// 所属工作区 ID
    /// </summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>
    /// 会话确定性 16 位哈希值（创建时由 SHA256(WorkspaceId + ":" + Id)[..16] 生成并永久落盘，供 ${session-hash} 使用）
    /// </summary>
    public string SessionHash { get; init; } = string.Empty;

    /// <summary>
    /// 会话标题（首轮用户提问后自动提取前 24 个字符生成，支持手动重命名）
    /// </summary>
    public string Title { get; set; } = "新建会话";

    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }

    public int MessageCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCachedTokens { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 统一消息体（支持 user / assistant / tool / system）
/// </summary>
public sealed class UnifiedChatMessage
{
    public string Id { get; init; } = $"msg_{Guid.NewGuid():N}"[..15];
    public string Role { get; set; } = "user"; // "system" | "user" | "assistant" | "tool"
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<UnifiedToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; } // 仅当 Role == "tool" 时有效，记录所响应的 call_id
    public string? Name { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CachedTokens { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

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
            ToolCalls = toolCalls != null ? new List<UnifiedToolCall>(toolCalls) : null,
            ReasoningContent = reasoningContent
        };

    public static UnifiedChatMessage Tool(string toolCallId, string output, string? name = null) =>
        new()
        {
            Role = "tool",
            ToolCallId = toolCallId,
            Content = output,
            Name = name
        };
}

/// <summary>
/// 上下文预算状态 DTO
/// </summary>
public sealed class ContextBudgetStatusDto
{
    public int TotalContextTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public int SafetyBuffer { get; set; } = 1024;
    public int UsedInputTokens { get; set; }
    public int AvailableInputTokens { get; set; }
    public double UsagePercentage { get; set; }
    public bool IsNearLimit { get; set; }
    public string? SessionHash { get; set; }
    public string? Protocol { get; set; }
}
