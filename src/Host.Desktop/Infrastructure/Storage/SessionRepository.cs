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

/// <summary>
/// 多工作区与多会话持久化仓储服务。
/// 管理 workspaces-index.json、sessions-index.json、meta.json 以及 messages.jsonl。
/// </summary>
public sealed partial class SessionRepository : ISessionRepository
{
    private readonly StoragePathManager _storageManager;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly SemaphoreSlim _workspacesIndexLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceSessionIndexLocks =
        new();

    public SessionRepository(StoragePathManager storageManager)
    {
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
    }

    public string GetWorkspacesIndexPath() =>
        Path.Combine(_storageManager.WorkspacesDirectory, "workspaces-index.json");

    public string GetWorkspaceDirectory(string workspaceId) =>
        Path.Combine(_storageManager.SessionsDirectory, workspaceId);

    public string GetSessionsIndexPath(string workspaceId) =>
        Path.Combine(_storageManager.SessionsDirectory, workspaceId, "sessions-index.json");

    public string GetSessionDirectory(string workspaceId, string sessionId) =>
        Path.Combine(_storageManager.SessionsDirectory, workspaceId, sessionId);

    public string GetSessionMetaPath(string workspaceId, string sessionId) =>
        Path.Combine(_storageManager.SessionsDirectory, workspaceId, sessionId, "meta.json");

    public string GetMessagesJsonlPath(string workspaceId, string sessionId) =>
        Path.Combine(_storageManager.SessionsDirectory, workspaceId, sessionId, "messages.jsonl");

    /// <summary>need09-05：会话附件根目录。</summary>
    public string GetAttachmentsDirectory(string workspaceId, string sessionId) =>
        ImageAttachmentStore.GetAttachmentsDir(
            _storageManager.CurrentDataRoot,
            workspaceId,
            sessionId
        );

    /// <summary>need09-05：保存图片附件（sha16 去重；只返回引用）。</summary>
    public Task<ChatImageRef> SaveImageAttachmentAsync(
        string workspaceId,
        string sessionId,
        string fileName,
        string mime,
        byte[] bytes,
        int width,
        int height,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        var (r, _) = ImageAttachmentStore.SaveImage(
            _storageManager.CurrentDataRoot,
            workspaceId,
            sessionId,
            fileName,
            mime,
            bytes,
            width,
            height
        );
        return Task.FromResult(r);
    }

    /// <summary>need09-05：按 imageId 定位附件（丢失返回 null）。</summary>
    public string? FindAttachmentPath(string workspaceId, string sessionId, string imageId) =>
        ImageAttachmentStore.FindImagePath(
            _storageManager.CurrentDataRoot,
            workspaceId,
            sessionId,
            imageId
        );

    /// <summary>need09-05：重载渲染一致性：逐引用核对文件存在性，丢失置 IsMissing（运行时标记，不落盘）。</summary>
    private void MarkMissingImages(string workspaceId, string sessionId, UnifiedChatMessage msg)
    {
        if (msg?.Images == null || msg.Images.Count == 0)
            return;
        foreach (var img in msg.Images)
        {
            if (img == null)
                continue;
            try
            {
                img.IsMissing = FindAttachmentPath(workspaceId, sessionId, img.ImageId) == null;
            }
            catch
            {
                img.IsMissing = true;
            }
        }
    }

    public async Task<List<WorkspaceInfo>> LoadWorkspacesIndexAsync(CancellationToken ct = default)
    {
        string path = GetWorkspacesIndexPath();
        if (!File.Exists(path))
        {
            return new List<WorkspaceInfo>();
        }

        await _workspacesIndexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct)
                .ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<WorkspaceInfo>>(
                json,
                HaloJsonOptions.IndentedCamelCase
            );
            return list ?? new List<WorkspaceInfo>();
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("Failed to load workspaces index", ex);
            return new List<WorkspaceInfo>();
        }
        finally
        {
            _workspacesIndexLock.Release();
        }
    }

    public async Task SaveWorkspacesIndexAsync(
        List<WorkspaceInfo> workspaces,
        CancellationToken ct = default
    )
    {
        string path = GetWorkspacesIndexPath();
        await _workspacesIndexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFileWriter.WriteJsonAtomicAsync(path, workspaces, ct).ConfigureAwait(false);
        }
        finally
        {
            _workspacesIndexLock.Release();
        }
    }

    public async Task<List<SessionMetadata>> LoadSessionsIndexAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return new List<SessionMetadata>();
        }

        string path = GetSessionsIndexPath(workspaceId);
        if (!File.Exists(path))
        {
            return new List<SessionMetadata>();
        }

        var wsLock = _workspaceSessionIndexLocks.GetOrAdd(
            workspaceId,
            _ => new SemaphoreSlim(1, 1)
        );
        await wsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct)
                .ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<SessionMetadata>>(
                json,
                HaloJsonOptions.IndentedCamelCase
            );
            list ??= new List<SessionMetadata>();
            // need04-01：读时修（内存态）：0001/缺字段收敛为可展示可排序
            foreach (var m in list)
                HealSessionTimes(m, null);
            return list;
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"Failed to load sessions index for workspace '{workspaceId}'",
                ex
            );
            return new List<SessionMetadata>();
        }
        finally
        {
            wsLock.Release();
        }
    }

    private static void HealSessionTimes(SessionMetadata m, string? metaPath)
    {
        try
        {
            bool badCreated = m.CreatedAtUtc == default || m.CreatedAtUtc.Year < 2000;
            bool badUpdated = m.UpdatedAtUtc == default || m.UpdatedAtUtc.Year < 2000;
            if (badCreated)
            {
                DateTime fallback = DateTime.UtcNow;
                if (!badUpdated)
                    fallback = DateTime.SpecifyKind(m.UpdatedAtUtc, DateTimeKind.Utc);
                else if (!string.IsNullOrEmpty(metaPath) && File.Exists(metaPath))
                {
                    try
                    {
                        fallback = File.GetCreationTimeUtc(metaPath);
                        if (fallback.Year < 2000)
                            fallback = DateTime.UtcNow;
                    }
                    catch
                    {
                        fallback = DateTime.UtcNow;
                    }
                }
                // CreatedAtUtc 为 init：老数据只能尽力，若仍 default 则保持（前端 isBadTime 兜底）
                // 此处通过反射式重建不可行，退化为：若 Updated 合法则不动 Created 显示逻辑交由前端；
                // 实际可写字段只有 UpdatedAtUtc：
                if (m.UpdatedAtUtc == default || m.UpdatedAtUtc.Year < 2000)
                    m.UpdatedAtUtc = fallback;
            }
            if (m.UpdatedAtUtc == default || m.UpdatedAtUtc.Year < 2000)
                m.UpdatedAtUtc = DateTime.UtcNow;
            // Updated 永不早于 Created（当 Created 合法时）
            if (
                m.CreatedAtUtc != default
                && m.CreatedAtUtc.Year >= 2000
                && m.UpdatedAtUtc < m.CreatedAtUtc
            )
                m.UpdatedAtUtc = m.CreatedAtUtc;
        }
        catch
        { /* 修复永不阻断读取 */
        }
    }

    public async Task SaveSessionsIndexAsync(
        string workspaceId,
        List<SessionMetadata> sessions,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        string path = GetSessionsIndexPath(workspaceId);
        var wsLock = _workspaceSessionIndexLocks.GetOrAdd(
            workspaceId,
            _ => new SemaphoreSlim(1, 1)
        );
        await wsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFileWriter.WriteJsonAtomicAsync(path, sessions, ct).ConfigureAwait(false);
        }
        finally
        {
            wsLock.Release();
        }
    }

    public async Task<SessionMetadata?> LoadSessionMetaAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        string path = GetSessionMetaPath(workspaceId, sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        SessionMetadata? meta = null;
        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct)
                .ConfigureAwait(false);
            meta = JsonSerializer.Deserialize<SessionMetadata>(
                json,
                HaloJsonOptions.IndentedCamelCase
            );
            if (meta != null)
            {
                bool badC = meta.CreatedAtUtc == default || meta.CreatedAtUtc.Year < 2000;
                bool badU =
                    meta.UpdatedAtUtc == default
                    || meta.UpdatedAtUtc.Year < 2000
                    || (meta.CreatedAtUtc.Year >= 2000 && meta.UpdatedAtUtc < meta.CreatedAtUtc);
                HealSessionTimes(meta, path);
                if ((badC || badU) && !(meta.CreatedAtUtc == default))
                {
                    try
                    {
                        await SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            return meta;
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"Failed to load session meta for session '{sessionId}' in ws '{workspaceId}'",
                ex
            );
            return meta;
        }
    }

    public async Task SaveSessionMetaAsync(
        string workspaceId,
        SessionMetadata meta,
        CancellationToken ct = default
    )
    {
        if (meta == null)
            throw new ArgumentNullException(nameof(meta));
        var sessionLock = _sessionLocks.GetOrAdd(meta.Id, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = GetSessionMetaPath(workspaceId, meta.Id);
            await AtomicFileWriter.WriteJsonAtomicAsync(path, meta, ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
