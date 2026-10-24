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
    /// 显示名称（默认取文件夹名；提示词 {{WorkspaceName}} 与请求头 ${workspace-name} 的实际数据源）。
    /// 注意：用户可见的个性化名称请用 Alias，勿与此字段混用。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 用户自定义别名（纯前端视觉；为空则界面回退显示 Name）。
    /// 永不进入提示词 / 请求头 / 上下文，仅用于侧边栏与顶栏展示。
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// 界面展示名：Alias 优先，空则回退 Name（运行时计算，不落盘）。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias!;

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
    public bool DirectoryExists =>
        !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);
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
    /// 会话标题（T1 首条 user 第一句话前 5 字即时生成；T2 首轮结束后精炼一次；手动重命名后 TitleLocked=true 永久冻结）
    /// </summary>
    public string Title { get; set; } = "新建会话";

    /// <summary>手动改名/自动精炼完成后冻结，Append 永不覆盖（老数据缺省 false 兼容）</summary>
    public bool TitleLocked { get; set; }

    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }

    public int MessageCount { get; set; }

    /// <summary>need05-01：占用快照=末轮 prompt（覆盖语义，有精确值则覆盖）。</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>need05-01：Σ账单=累计 completion（只增不减，compact/clear 外永不减少）。</summary>
    public long TotalOutputTokens { get; set; }

    /// <summary>need05-01：占用快照的子集（覆盖语义，恒 ≤ TotalInputTokens）。</summary>
    public long TotalCachedTokens { get; set; }

    /// <summary>need05-01：Σ账单=累计 prompt，每次 usage.prompt 到账累加，compact 重写时保留，clear 时清零。</summary>
    public long CumulativePromptTokens { get; set; }

    /// <summary>need05-01：Σ账单=累计 completion，每次 usage.completion 到账累加。</summary>
    public long CumulativeCompletionTokens { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>need13：父会话 ID（null/空 = Main；非空 = 寄宿于该主的子会话）。</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>need13：main|sub（缺省 main，老数据兼容）。</summary>
    public string AgentRole { get; set; } = "main";

    /// <summary>need13：spawn 时 label（≤20 字，索引用）。</summary>
    public string? SubLabel { get; set; }

    /// <summary>need13：pending|running|succeeded|failed|cancelled（诊断用，内存+落盘双写）。</summary>
    public string? SubStatus { get; set; }

    /// <summary>need13：由主轮哪次 tool_call 创建（callId，链路追踪）。</summary>
    public string? CreatedByCallId { get; set; }

    /// <summary>need13：task 截断 2000 字（索引用，全文不存 meta）。</summary>
    public string? TaskPrompt { get; set; }
}

/// <summary>
/// need09-05：会话内图片引用（messages.jsonl 只存引用不存 base64；字节落盘 attachments/）。
/// </summary>
public sealed class ChatImageRef
{
    /// <summary>图片唯一 ID（img_ + sha16）。</summary>
    public string ImageId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>image/jpeg | image/png | image/webp。</summary>
    public string Mime { get; set; } = string.Empty;

    /// <summary>会话内相对路径 attachments/日期/imageId_safeName。</summary>
    public string RelPath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>图片字节 SHA256 hex 前 16 位（去重键）。</summary>
    public string Sha16 { get; set; } = string.Empty;

    /// <summary>运行时丢失标记（文件被删）；JsonIgnore 永不落盘，读时重算。</summary>
    [JsonIgnore]
    public bool IsMissing { get; set; }
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

    /// <summary>need09-05：本消息附带的图片引用；null/空 = 纯文本（STJ 缺字段容忍，WhenWritingNull 保证老快照零抖动）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatImageRef>? Images { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public static UnifiedChatMessage System(string content) =>
        new() { Role = "system", Content = content };

    public static UnifiedChatMessage User(string content) =>
        new() { Role = "user", Content = content };

    public static UnifiedChatMessage Assistant(
        string? content,
        IReadOnlyList<UnifiedToolCall>? toolCalls = null,
        string? reasoningContent = null
    ) =>
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

    /// <summary>need05-01：精确占用快照（末轮 prompt），null = unknown（无快照）。</summary>
    public int? LastPromptTokens { get; set; }

    /// <summary>need05-01：占用快照子集，null = unknown；非 null 时恒 ≤ LastPromptTokens。</summary>
    public int? LastCachedTokens { get; set; }

    /// <summary>need05-01：= cached/input；input&lt;=0 或任一 null 则 null（未知不画 0%）。</summary>
    public double? CacheHitRate { get; set; }

    /// <summary>need05-01：true = UsedInputTokens 来自估算回退（流式中/无快照）。</summary>
    public bool UsedInputIsEstimated { get; set; }

    /// <summary>need05-01：known | unknown | estimated。空会话为 known+0%；有消息无快照为 unknown。</summary>
    public string OccupancyState { get; set; } = "known";

    /// <summary>need05-01：Σ账单 prompt（拷贝 meta.CumulativePromptTokens）。</summary>
    public long BilledPromptTokens { get; set; }

    /// <summary>need05-01：Σ账单 completion。</summary>
    public long BilledCompletionTokens { get; set; }

    /// <summary>need05-06：本地预测命中率（发送前分块 LCP 推测，无实测时前端灰色 ~ 显示）。</summary>
    public double? PredictedCacheHitRate { get; set; }

    /// <summary>need05-06：本地预测命中 tokens（= 稳定前缀 tokens）。</summary>
    public int? PredictedCachedTokens { get; set; }

    /// <summary>need05-06：拼接对比断裂块名（stable/- 首轮无依据）。</summary>
    public string? PrefixDivergenceBlock { get; set; }
}

/// <summary>need07：历史截断结果（重放用）。</summary>
public sealed class HistoryTruncateResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public int KeptMessages { get; init; }
    public int RemovedMessages { get; init; }
    public string? ReplayText { get; init; }
    public string? BackupFileName { get; init; }

    public static HistoryTruncateResult Ok(int kept, int removed, string? replay, string? bak) =>
        new()
        {
            Success = true,
            KeptMessages = kept,
            RemovedMessages = removed,
            ReplayText = replay,
            BackupFileName = bak
        };

    public static HistoryTruncateResult Fail(string code) =>
        new() { Success = false, ErrorCode = code };
}

/// <summary>孤儿 tool_calls 愈合结果：补 synthetic cancelled tool + 清理无主 tool 消息。</summary>
public sealed class HistoryHealResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>补上的 synthetic cancelled tool 消息数。</summary>
    public int SyntheticAppended { get; init; }

    /// <summary>清理掉的无主 tool 消息数（找不到归属 assistant tool_call）。</summary>
    public int OrphanToolsRemoved { get; init; }
    public int KeptMessages { get; init; }
    public string? BackupFileName { get; init; }

    public static HistoryHealResult Ok(int synthetic, int removed, int kept, string? bak) =>
        new()
        {
            Success = true,
            SyntheticAppended = synthetic,
            OrphanToolsRemoved = removed,
            KeptMessages = kept,
            BackupFileName = bak
        };

    public static HistoryHealResult Clean(int kept) =>
        new() { Success = true, KeptMessages = kept };

    public static HistoryHealResult Fail(string code) =>
        new() { Success = false, ErrorCode = code };
}

/// <summary>单条消息删除结果（删除后顺带做一次孤儿愈合，保证历史仍可直接发模型）。</summary>
public sealed class HistoryDeleteResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public int KeptMessages { get; init; }
    public int SyntheticAppended { get; init; }
    public int OrphanToolsRemoved { get; init; }
    public string? BackupFileName { get; init; }

    public static HistoryDeleteResult Ok(int kept, int synthetic, int removed, string? bak) =>
        new()
        {
            Success = true,
            KeptMessages = kept,
            SyntheticAppended = synthetic,
            OrphanToolsRemoved = removed,
            BackupFileName = bak
        };

    public static HistoryDeleteResult Fail(string code) =>
        new() { Success = false, ErrorCode = code };
}
