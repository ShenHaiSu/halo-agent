using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

public interface ISessionRepository
{
    Task<List<WorkspaceInfo>> LoadWorkspacesIndexAsync(CancellationToken ct = default);
    Task SaveWorkspacesIndexAsync(List<WorkspaceInfo> workspaces, CancellationToken ct = default);
    Task<List<SessionMetadata>> LoadSessionsIndexAsync(
        string workspaceId,
        CancellationToken ct = default
    );
    Task SaveSessionsIndexAsync(
        string workspaceId,
        List<SessionMetadata> sessions,
        CancellationToken ct = default
    );
    Task<SessionMetadata?> LoadSessionMetaAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task SaveSessionMetaAsync(
        string workspaceId,
        SessionMetadata meta,
        CancellationToken ct = default
    );
    Task AppendMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    );
    Task<List<UnifiedChatMessage>> GetMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    IAsyncEnumerable<UnifiedChatMessage> StreamMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<bool> DeleteWorkspaceDirectoryAsync(string workspaceId, CancellationToken ct = default);
    Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>need05-03：重写前全量备份 messages.jsonl，返回备份文件名。</summary>
    Task<string> BackupMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>need07：同上，允许调用方指定备份文件前缀（默认 compact-bak，保持老调用零改动）。</summary>
    Task<string> BackupMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        string filePrefix,
        CancellationToken ct = default
    );

    /// <summary>need05-03：原子重写 messages.jsonl（tmp→move，避免半截文件）。</summary>
    Task RewriteMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> messages,
        CancellationToken ct = default
    );
    string GetWorkspaceDirectory(string workspaceId);
    string GetSessionDirectory(string workspaceId, string sessionId);
    string GetMessagesJsonlPath(string workspaceId, string sessionId);

    /// <summary>need09-05：会话附件根目录 sessions/&lt;ws&gt;/&lt;sess&gt;/attachments。</summary>
    string GetAttachmentsDirectory(string workspaceId, string sessionId);

    /// <summary>need09-05：保存图片附件（sha16 去重，只存引用）。</summary>
    Task<ChatImageRef> SaveImageAttachmentAsync(
        string workspaceId,
        string sessionId,
        string fileName,
        string mime,
        byte[] bytes,
        int width,
        int height,
        CancellationToken ct = default
    );

    /// <summary>need09-05：按 imageId 定位附件绝对路径（丢失返回 null）。</summary>
    string? FindAttachmentPath(string workspaceId, string sessionId, string imageId);
}
