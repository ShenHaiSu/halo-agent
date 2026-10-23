using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

public interface IWorkspaceSessionService
{
    Task<List<WorkspaceInfo>> GetAllWorkspacesAsync(CancellationToken ct = default);
    Task<WorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default);
    Task<WorkspaceInfo> AddOrUpdateWorkspaceAsync(
        string rootPath,
        string? customName = null,
        CancellationToken ct = default
    );
    Task<bool> RemoveWorkspaceAsync(
        string workspaceId,
        bool deletePhysicalSessionFolder = true,
        CancellationToken ct = default
    );
    Task<bool> SetWorkspacePreferenceAsync(
        string workspaceId,
        string? providerId,
        string? modelId,
        CancellationToken ct = default
    );
    Task<bool> TouchWorkspaceAsync(string workspaceId, CancellationToken ct = default);

    Task<List<SessionMetadata>> GetSessionsByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    );
    Task<SessionMetadata> CreateSessionAsync(
        string workspaceId,
        string? title = null,
        string? providerId = null,
        string? modelId = null,
        CancellationToken ct = default
    );

    /// <summary>need13：建子会话（扁平寄宿；老 CreateSessionAsync 委托此方法 parent=null）。</summary>
    Task<SessionMetadata> CreateSubSessionAsync(
        string workspaceId,
        string? title,
        string? providerId,
        string? modelId,
        string? parentSessionId,
        string agentRole,
        string? subLabel,
        string? createdByCallId,
        string? taskPrompt,
        CancellationToken ct = default
    );

    /// <summary>need13：查主下全部子（创建时间正序）。</summary>
    Task<List<SessionMetadata>> GetSubSessionsAsync(
        string workspaceId,
        string parentSessionId,
        CancellationToken ct = default
    );
    Task<SessionMetadata?> GetSubSessionAsync(
        string workspaceId,
        string parentSessionId,
        string subSessionId,
        CancellationToken ct = default
    );

    /// <summary>need13：级联删树（先取消由 Bridge 层调 CancelTree，再删子盘+主盘）。</summary>
    Task<bool> DeleteSessionTreeAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<SessionMetadata?> GetSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<List<UnifiedChatMessage>> GetSessionMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<bool> AppendSessionMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    );

    /// <summary>need07：重放截断（removeAnchor=true 为重放模式：按前驱 user 切面并返回复发原文）。</summary>
    Task<HistoryTruncateResult> TruncateSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        bool removeAnchor,
        CancellationToken ct = default
    );

    /// <summary>need07：分支（includeAnchor=true 含锚轮，false 排除锚点）。</summary>
    Task<SessionMetadata> BranchSessionAsync(
        string workspaceId,
        string sourceSessionId,
        string anchorMessageId,
        bool includeAnchor,
        CancellationToken ct = default
    );

    /// <summary>孤儿 tool_calls 愈合：缺回包的补 synthetic cancelled tool，无主 tool 消息直接清理。</summary>
    Task<HistoryHealResult> HealSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>删除单条消息（删后顺带愈合，保证历史仍可直接发模型）。</summary>
    Task<HistoryDeleteResult> DeleteSessionMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        CancellationToken ct = default
    );
    Task<bool> UpdateSessionTitleAsync(
        string workspaceId,
        string sessionId,
        string newTitle,
        CancellationToken ct = default
    );
    Task<bool> SetSessionPinnedAsync(
        string workspaceId,
        string sessionId,
        bool isPinned,
        CancellationToken ct = default
    );
    Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
}
