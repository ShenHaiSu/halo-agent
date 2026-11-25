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
    StoragePathManager Storage { get; }
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

    /// <summary>
    /// 在同一把工作区锁内完成 sessions-index.json 的读-改-写，避免并发写互相回退元数据。
    /// mutate 回调在锁内执行，禁止在其中调用会再次获取工作区锁的方法。
    /// </summary>
    Task MutateSessionsIndexAsync(
        string workspaceId,
        Action<List<SessionMetadata>> mutate,
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

    /// <summary>P007：活跃切片读（v2 DB 单语句快路径；无库/失败回退全量）。无桩会话等价于全量。</summary>
    Task<List<UnifiedChatMessage>> GetActiveContextMessagesAsync(
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

    /// <summary>
    /// 严格回滚：分别验证数据库分区、会话元数据、目录与索引，任何失败都记录在结果中，
    /// 不吞错、不把残留误报为已清理。
    /// </summary>
    Task<HistoryRollbackResult> RollbackCreatedSessionAsync(
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

    string GetWorkspaceDirectory(string workspaceId);
    string GetSessionDirectory(string workspaceId, string sessionId);

    /// <summary>会话附件根目录 sessions/&lt;ws&gt;/&lt;sess&gt;/attachments。</summary>
    string GetAttachmentsDirectory(string workspaceId, string sessionId);

    /// <summary>保存图片附件（sha16 去重，只返回引用）。</summary>
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

    /// <summary>按 imageId 定位附件绝对路径（丢失返回 null）。</summary>
    string? FindAttachmentPath(string workspaceId, string sessionId, string imageId);
}
