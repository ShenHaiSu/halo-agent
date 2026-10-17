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
    Task<List<SessionMetadata>> LoadSessionsIndexAsync(string workspaceId, CancellationToken ct = default);
    Task SaveSessionsIndexAsync(string workspaceId, List<SessionMetadata> sessions, CancellationToken ct = default);
    Task<SessionMetadata?> LoadSessionMetaAsync(string workspaceId, string sessionId, CancellationToken ct = default);
    Task SaveSessionMetaAsync(string workspaceId, SessionMetadata meta, CancellationToken ct = default);
    Task AppendMessageAsync(string workspaceId, string sessionId, UnifiedChatMessage message, CancellationToken ct = default);
    Task<List<UnifiedChatMessage>> GetMessagesAsync(string workspaceId, string sessionId, CancellationToken ct = default);
    IAsyncEnumerable<UnifiedChatMessage> StreamMessagesAsync(string workspaceId, string sessionId, CancellationToken ct = default);
    Task<bool> DeleteSessionAsync(string workspaceId, string sessionId, CancellationToken ct = default);
    Task<bool> DeleteWorkspaceDirectoryAsync(string workspaceId, CancellationToken ct = default);
    Task<string> ExportSessionMarkdownAsync(string workspaceId, string sessionId, CancellationToken ct = default);
     /// <summary>need05-03：重写前全量备份 messages.jsonl，返回备份文件名。</summary>
     Task<string> BackupMessagesJsonlAsync(string workspaceId, string sessionId, CancellationToken ct = default);
     /// <summary>need07：同上，允许调用方指定备份文件前缀（默认 compact-bak，保持老调用零改动）。</summary>
     Task<string> BackupMessagesJsonlAsync(string workspaceId, string sessionId, string filePrefix, CancellationToken ct = default);
     /// <summary>need05-03：原子重写 messages.jsonl（tmp→move，避免半截文件）。</summary>
     Task RewriteMessagesJsonlAsync(string workspaceId, string sessionId, IReadOnlyList<UnifiedChatMessage> messages, CancellationToken ct = default);
    string GetWorkspaceDirectory(string workspaceId);
    string GetSessionDirectory(string workspaceId, string sessionId);
    string GetMessagesJsonlPath(string workspaceId, string sessionId);
    /// <summary>need09-05：会话附件根目录 sessions/&lt;ws&gt;/&lt;sess&gt;/attachments。</summary>
    string GetAttachmentsDirectory(string workspaceId, string sessionId);
    /// <summary>need09-05：保存图片附件（sha16 去重，只存引用）。</summary>
    Task<ChatImageRef> SaveImageAttachmentAsync(string workspaceId, string sessionId, string fileName, string mime, byte[] bytes, int width, int height, CancellationToken ct = default);
    /// <summary>need09-05：按 imageId 定位附件绝对路径（丢失返回 null）。</summary>
    string? FindAttachmentPath(string workspaceId, string sessionId, string imageId);
}

/// <summary>
/// 多工作区与多会话持久化仓储服务。
/// 管理 workspaces-index.json、sessions-index.json、meta.json 以及 messages.jsonl。
/// </summary>
public sealed class SessionRepository : ISessionRepository
{
    private readonly StoragePathManager _storageManager;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly SemaphoreSlim _workspacesIndexLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceSessionIndexLocks = new();

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
        ImageAttachmentStore.GetAttachmentsDir(_storageManager.CurrentDataRoot, workspaceId, sessionId);
    /// <summary>need09-05：保存图片附件（sha16 去重；只返回引用）。</summary>
    public Task<ChatImageRef> SaveImageAttachmentAsync(string workspaceId, string sessionId, string fileName, string mime, byte[] bytes, int width, int height, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (r, _) = ImageAttachmentStore.SaveImage(_storageManager.CurrentDataRoot, workspaceId, sessionId, fileName, mime, bytes, width, height);
        return Task.FromResult(r);
    }
    /// <summary>need09-05：按 imageId 定位附件（丢失返回 null）。</summary>
    public string? FindAttachmentPath(string workspaceId, string sessionId, string imageId) =>
        ImageAttachmentStore.FindImagePath(_storageManager.CurrentDataRoot, workspaceId, sessionId, imageId);
    /// <summary>need09-05：重载渲染一致性：逐引用核对文件存在性，丢失置 IsMissing（运行时标记，不落盘）。</summary>
    private void MarkMissingImages(string workspaceId, string sessionId, UnifiedChatMessage msg)
    {
        if (msg?.Images == null || msg.Images.Count == 0) return;
        foreach (var img in msg.Images)
        {
            if (img == null) continue;
            try { img.IsMissing = FindAttachmentPath(workspaceId, sessionId, img.ImageId) == null; }
            catch { img.IsMissing = true; }
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
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<WorkspaceInfo>>(json, HaloJsonOptions.IndentedCamelCase);
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

    public async Task SaveWorkspacesIndexAsync(List<WorkspaceInfo> workspaces, CancellationToken ct = default)
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

    public async Task<List<SessionMetadata>> LoadSessionsIndexAsync(string workspaceId, CancellationToken ct = default)
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

         var wsLock = _workspaceSessionIndexLocks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
         await wsLock.WaitAsync(ct).ConfigureAwait(false);
         try
         {
             string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
             var list = JsonSerializer.Deserialize<List<SessionMetadata>>(json, HaloJsonOptions.IndentedCamelCase);
             list ??= new List<SessionMetadata>();
             // need04-01：读时修（内存态）：0001/缺字段收敛为可展示可排序
             foreach (var m in list) HealSessionTimes(m, null);
             return list;
         }
         catch (Exception ex)
         {
             HaloLogger.App.Error($"Failed to load sessions index for workspace '{workspaceId}'", ex);
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
                 if (!badUpdated) fallback = DateTime.SpecifyKind(m.UpdatedAtUtc, DateTimeKind.Utc);
                 else if (!string.IsNullOrEmpty(metaPath) && File.Exists(metaPath))
                 {
                     try { fallback = File.GetCreationTimeUtc(metaPath); if (fallback.Year < 2000) fallback = DateTime.UtcNow; } catch { fallback = DateTime.UtcNow; }
                 }
                 // CreatedAtUtc 为 init：老数据只能尽力，若仍 default 则保持（前端 isBadTime 兜底）
                 // 此处通过反射式重建不可行，退化为：若 Updated 合法则不动 Created 显示逻辑交由前端；
                 // 实际可写字段只有 UpdatedAtUtc：
                 if (m.UpdatedAtUtc == default || m.UpdatedAtUtc.Year < 2000) m.UpdatedAtUtc = fallback;
             }
             if (m.UpdatedAtUtc == default || m.UpdatedAtUtc.Year < 2000) m.UpdatedAtUtc = DateTime.UtcNow;
             // Updated 永不早于 Created（当 Created 合法时）
             if (m.CreatedAtUtc != default && m.CreatedAtUtc.Year >= 2000 && m.UpdatedAtUtc < m.CreatedAtUtc)
                 m.UpdatedAtUtc = m.CreatedAtUtc;
         }
         catch { /* 修复永不阻断读取 */ }
     }

    public async Task SaveSessionsIndexAsync(string workspaceId, List<SessionMetadata> sessions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        string path = GetSessionsIndexPath(workspaceId);
        var wsLock = _workspaceSessionIndexLocks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
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

    public async Task<SessionMetadata?> LoadSessionMetaAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        string path = GetSessionMetaPath(workspaceId, sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        SessionMetadata? meta = null;
        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            meta = JsonSerializer.Deserialize<SessionMetadata>(json, HaloJsonOptions.IndentedCamelCase);
            if (meta != null)
            {
                bool badC = meta.CreatedAtUtc == default || meta.CreatedAtUtc.Year < 2000;
                bool badU = meta.UpdatedAtUtc == default || meta.UpdatedAtUtc.Year < 2000 || (meta.CreatedAtUtc.Year >= 2000 && meta.UpdatedAtUtc < meta.CreatedAtUtc);
                HealSessionTimes(meta, path);
                if ((badC || badU) && !(meta.CreatedAtUtc == default))
                {
                    try { await SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false); } catch { }
                }
            }
            return meta;
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"Failed to load session meta for session '{sessionId}' in ws '{workspaceId}'", ex);
            return meta;
        }
    }

    public async Task SaveSessionMetaAsync(string workspaceId, SessionMetadata meta, CancellationToken ct = default)
    {
        if (meta == null) throw new ArgumentNullException(nameof(meta));
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

    public async Task AppendMessageAsync(string workspaceId, string sessionId, UnifiedChatMessage message, CancellationToken ct = default)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
            var dir = Path.GetDirectoryName(jsonlPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string line = JsonSerializer.Serialize(message, HaloJsonOptions.CompactCamelCase) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);

            await using var fs = new FileStream(
                jsonlPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true);
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<List<UnifiedChatMessage>> GetMessagesAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        if (!File.Exists(jsonlPath))
        {
            return new List<UnifiedChatMessage>();
        }

        var list = new List<UnifiedChatMessage>();
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var msg = JsonSerializer.Deserialize<UnifiedChatMessage>(line, HaloJsonOptions.CompactCamelCase);
                    if (msg != null)
                    {
                        MarkMissingImages(workspaceId, sessionId, msg);
                        list.Add(msg);
                    }
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn($"Failed to deserialize message line in session '{sessionId}': {ex.Message}");
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }
        return list;
    }

    public async IAsyncEnumerable<UnifiedChatMessage> StreamMessagesAsync(
        string workspaceId,
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        if (!File.Exists(jsonlPath))
        {
            yield break;
        }

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                UnifiedChatMessage? msg = null;
                try
                {
                    msg = JsonSerializer.Deserialize<UnifiedChatMessage>(line, HaloJsonOptions.CompactCamelCase);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn($"Failed to deserialize message line in session '{sessionId}': {ex.Message}");
                }

                if (msg != null)
                {
                    MarkMissingImages(workspaceId, sessionId, msg);
                    yield return msg;
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<bool> DeleteSessionAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        string sessionDir = GetSessionDirectory(workspaceId, sessionId);
        if (Directory.Exists(sessionDir))
        {
            try
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"Failed to delete session directory '{sessionDir}': {ex.Message}");
            }
        }

        var sessions = await LoadSessionsIndexAsync(workspaceId, ct).ConfigureAwait(false);
        int removed = sessions.RemoveAll(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await SaveSessionsIndexAsync(workspaceId, sessions, ct).ConfigureAwait(false);
        }
        return true;
    }

    public Task<bool> DeleteWorkspaceDirectoryAsync(string workspaceId, CancellationToken ct = default)
    {
        string wsDir = GetWorkspaceDirectory(workspaceId);
        if (Directory.Exists(wsDir))
        {
            try
            {
                Directory.Delete(wsDir, recursive: true);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"Failed to delete workspace directory '{wsDir}': {ex.Message}");
                return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// need05-03：备份 messages.jsonl → messages.compact-bak-{UTC:yyyyMMddHHmmss}.jsonl，返回备份文件名。
    /// 与 AppendMessageAsync 持同一把 _sessionLocks[sessionId] 锁，天然互斥。
    /// </summary>
     public Task<string> BackupMessagesJsonlAsync(string workspaceId, string sessionId, CancellationToken ct = default)
         => BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.compact-bak-", ct);
     /// <summary>need07：前缀可配备份（replay-bak / compact-bak 共用同一原子拷贝路径）。</summary>
     public async Task<string> BackupMessagesJsonlAsync(string workspaceId, string sessionId, string filePrefix, CancellationToken ct = default)
     {
         string prefix = string.IsNullOrWhiteSpace(filePrefix) ? "messages.compact-bak-" : filePrefix.Trim();
         string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
         string backupFileName = $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl";
         string backupPath = Path.Combine(GetSessionDirectory(workspaceId, sessionId), backupFileName);
         var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
         await sessionLock.WaitAsync(ct).ConfigureAwait(false);
         try
         {
             var dir = Path.GetDirectoryName(backupPath);
             if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
             if (File.Exists(jsonlPath))
             {
                 using var src = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                 using var dst = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                 await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                 await dst.FlushAsync(ct).ConfigureAwait(false);
             }
             else
             {
                 await File.WriteAllTextAsync(backupPath, string.Empty, Encoding.UTF8, ct).ConfigureAwait(false);
             }
         }
         finally
         {
             sessionLock.Release();
         }
         return backupFileName;
     }

    /// <summary>
    /// need05-03：原子重写 messages.jsonl（tmp→move，避免半截文件；任一时刻可被 GetMessagesAsync 完整解析）。
    /// </summary>
    public async Task RewriteMessagesJsonlAsync(string workspaceId, string sessionId, IReadOnlyList<UnifiedChatMessage> messages, CancellationToken ct = default)
    {
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(jsonlPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmpPath = jsonlPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    foreach (var m in messages)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line = JsonSerializer.Serialize(m, HaloJsonOptions.CompactCamelCase) + "\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(line);
                        await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Move(tmpPath, jsonlPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* ignore */ }
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<string> ExportSessionMarkdownAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        var meta = await LoadSessionMetaAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        var messages = await GetMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"# {meta?.Title ?? "会话记录"}");
        sb.AppendLine();
        sb.AppendLine($"- **Session ID**: `{sessionId}`");
        if (!string.IsNullOrEmpty(meta?.SessionHash))
        {
            sb.AppendLine($"- **Session Hash**: `{meta.SessionHash}`");
        }
        if (!string.IsNullOrEmpty(meta?.ProviderId) || !string.IsNullOrEmpty(meta?.ModelId))
        {
            sb.AppendLine($"- **Model**: `{meta?.ProviderId} / {meta?.ModelId}`");
        }
        sb.AppendLine($"- **Created At**: `{meta?.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC`");
        sb.AppendLine($"- **Message Count**: {messages.Count}");
        // need05-01：占用/累计分开口径（旧 meta 缺键显示 ?，绝不把未知画成 0）
        string occIn = (meta != null && meta.TotalInputTokens > 0) ? meta.TotalInputTokens.ToString() : "?";
        string billP = (meta != null && (meta.CumulativePromptTokens > 0 || meta.TotalInputTokens > 0)) ? meta.CumulativePromptTokens.ToString() : "?";
        string billC = (meta != null && (meta.CumulativeCompletionTokens > 0 || meta.TotalOutputTokens > 0)) ? meta.CumulativeCompletionTokens.ToString() : "?";
        sb.AppendLine($"- 占用 IN {occIn}（快照=末轮 prompt） / 本轮 OUT 按轮 / 累计 Σ {billP}+{billC}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            sb.AppendLine($"### [{msg.Role.ToUpperInvariant()}] `{msg.CreatedAtUtc:HH:mm:ss}`");
            if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
            {
                sb.AppendLine();
                sb.AppendLine("> **思考过程**:");
                foreach (var line in msg.ReasoningContent.Split('\n'))
                {
                    sb.AppendLine($"> {line.TrimEnd()}");
                }
            }
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine();
                sb.AppendLine(msg.Content);
            }
            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**工具调用**:");
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"- `{tc.FunctionName}` (ID: `{tc.CallId}`):");
                    sb.AppendLine("```json");
                    sb.AppendLine(tc.ArgumentsJson);
                    sb.AppendLine("```");
                }
            }
            if (!string.IsNullOrWhiteSpace(msg.ToolCallId))
            {
                sb.AppendLine($"- 对应调用 ID: `{msg.ToolCallId}`");
            }
            if (msg.Images != null && msg.Images.Count > 0)
            {
                foreach (var img in msg.Images)
                {
                    if (img == null) continue;
                    if (img.IsMissing) sb.AppendLine($"[图片已丢失：{img.FileName}]");
                    else sb.AppendLine($"![{img.FileName}]({img.RelPath})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// need09-05：会话图片附件存储（引用落 messages.jsonl，字节落 attachments/；sha16 去重；只存引用不存 base64）。
/// 由 SessionRepository / ViewImageTool / Bridge 共享；Orchestrator 不直接依赖。
/// </summary>
public static class ImageAttachmentStore
{
    public const long MaxImageBytes = 8L * 1024 * 1024;
    public const int NormalizeMaxEdge = 2048;
    public const int ThumbnailMaxEdge = 256;
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly object _writeLock = new();
    /// <summary>need09-05：支持的 mime（jpg/png/webp）。</summary>
    public static bool IsSupportedExtension(string? fileName)
    {
        string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp";
    }
    /// <summary>need09-05：按扩展名推断 mime。</summary>
    public static string MimeFromExtension(string? fileName)
    {
        string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
    }
    /// <summary>need09-05：魔数校验（FFD8/PNG89/RIFF-WEBP）。</summary>
    public static bool CheckMagic(byte[] bytes, string mime)
    {
        if (bytes == null || bytes.Length < 12) return false;
        if (mime == "image/png")
            return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        if (mime == "image/jpeg")
            return bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (mime == "image/webp")
            return bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        return false;
    }
    /// <summary>need09-05：SHA256 hex 前 16 位（去重键）。</summary>
    public static string ComputeSha16(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
    /// <summary>need09-05：文件名清洗（非法字符→_，限长 64，空回退 image）。</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "image";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Trim().Length);
        foreach (char c in name.Trim()) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string r = sb.ToString().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(r)) r = "image";
        return r.Length > 64 ? r.Substring(0, 64) : r;
    }
    public static string GetAttachmentsDir(string dataRoot, string workspaceId, string sessionId) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot, "sessions", workspaceId, sessionId, "attachments");
     /// <summary>need09-05：按 imageId 扫描附件目录（跨日期去重命中即复用）。</summary>
     public static string? FindImagePath(string dataRoot, string workspaceId, string sessionId, string imageId)
     {
         try
         {
             string dir = GetAttachmentsDir(dataRoot, workspaceId, sessionId);
             if (!Directory.Exists(dir)) return null;
             foreach (string dateDir in Directory.GetDirectories(dir))
             {
                 string[] hits = Directory.GetFiles(dateDir, imageId + "_*");
                 if (hits.Length > 0) return hits.OrderBy(h => h, StringComparer.Ordinal).First();
             }
         }
         catch { /* 查找永不抛 */ }
         return null;
     }
     public static string? ResolveDataUrl(string dataRoot, string workspaceId, string sessionId, ChatImageRef img)
     {
         try
         {
             if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
             string? abs = FindImagePath(dataRoot, workspaceId, sessionId, img.ImageId.Trim());
             if (abs == null || !File.Exists(abs)) return null;
             byte[] bytes = File.ReadAllBytes(abs);
             if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
             string mime = MimeFromExtension(abs);
             if (!CheckMagic(bytes, mime)) return null;
             return ToDataUrl(bytes, mime);
         }
         catch { return null; }
     }
    /// <summary>need09-05：保存图片附件（sha16 去重；返回引用，IsNew 标识是否新落盘）。</summary>
    public static (ChatImageRef Ref, bool IsNew) SaveImage(string dataRoot, string workspaceId, string sessionId, string fileName, string mime, byte[] bytes, int width, int height)
    {
        string sha16 = ComputeSha16(bytes);
        string imageId = "img_" + sha16;
        lock (_writeLock)
        {
            string? existing = FindImagePath(dataRoot, workspaceId, sessionId, imageId);
            if (existing != null)
            {
                var fi = new FileInfo(existing);
                string rel = Path.GetRelativePath(Path.Combine(string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot, "sessions", workspaceId, sessionId), existing).Replace(Path.DirectorySeparatorChar, '/');
                return (new ChatImageRef { ImageId = imageId, FileName = Path.GetFileName(existing).Substring(imageId.Length + 1), Mime = MimeFromExtension(existing), RelPath = rel, Width = width, Height = height, SizeBytes = fi.Length, Sha16 = sha16 }, false);
            }
            string safe = SanitizeFileName(fileName);
            string date = DateTime.UtcNow.ToString("yyyyMMdd");
            string dir = Path.Combine(GetAttachmentsDir(dataRoot, workspaceId, sessionId), date);
            Directory.CreateDirectory(dir);
            string abs = Path.Combine(dir, imageId + "_" + safe);
            File.WriteAllBytes(abs, bytes);
            string sessionDir = Path.Combine(string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot, "sessions", workspaceId, sessionId);
            string relPath = Path.GetRelativePath(sessionDir, abs).Replace(Path.DirectorySeparatorChar, '/');
            return (new ChatImageRef { ImageId = imageId, FileName = safe, Mime = mime, RelPath = relPath, Width = width, Height = height, SizeBytes = bytes.LongLength, Sha16 = sha16 }, true);
        }
    }
    /// <summary>need09-05：解析图片宽高（PNG-IHDR / JPEG-SOF / WEBP-VP8 系；失败返回 0,0）。</summary>
    public static (int Width, int Height) TryParseDimensions(byte[] bytes, string mime)
    {
        try
        {
            if (bytes == null || bytes.Length < 24) return (0, 0);
            if (mime == "image/png" && bytes.Length >= 24)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w > 0 && w < 100000 && h > 0 && h < 100000) ? (w, h) : (0, 0);
            }
            if (mime == "image/jpeg")
            {
                int i = 2;
                while (i + 8 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) break;
                    byte marker = bytes[i + 1];
                    if (marker == 0xD8 || marker == 0xD9) { i += 2; continue; }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int len = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (len < 2) break;
                    bool isSof = (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC);
                    if (isSof && i + 8 < bytes.Length)
                    {
                        int h = (bytes[i + 5] << 8) | bytes[i + 6];
                        int w = (bytes[i + 7] << 8) | bytes[i + 8];
                        return (w > 0 && h > 0) ? (w, h) : (0, 0);
                    }
                    i += 2 + len;
                }
                return (0, 0);
            }
            if (mime == "image/webp" && bytes.Length >= 30)
            {
                string chunk = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
                if (chunk == "VP8X" && bytes.Length >= 30)
                {
                    int w = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
                    int h = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
                    return (w > 0 && h > 0) ? (w, h) : (0, 0);
                }
                if (chunk == "VP8 " && bytes.Length >= 30)
                {
                    int w = bytes[26] | (bytes[27] << 8);
                    int h = bytes[28] | (bytes[29] << 8);
                    w &= 0x3FFF; h &= 0x3FFF;
                    return (w > 0 && h > 0) ? (w, h) : (0, 0);
                }
                if (chunk == "VP8L" && bytes.Length >= 25)
                {
                    int b0 = bytes[21], b1 = bytes[22], b2 = bytes[23], b3 = bytes[24];
                    int w = ((b1 & 0x3F) << 8 | b0) + 1;
                    int h = ((b3 & 0x0F) << 10 | (b2 << 2) | ((b1 & 0xC0) >> 6)) + 1;
                    return (w > 0 && h > 0 && w < 100000 && h < 100000) ? (w, h) : (0, 0);
                }
            }
        }
        catch { /* 解析永不抛 */ }
        return (0, 0);
    }
    /// <summary>need09-05：归一化（长边≤maxEdge 才原样；超限则 System.Drawing 等比缩放转 JPEG q85；失败回退原图）。</summary>
    public static (byte[] Bytes, string Mime, int Width, int Height) NormalizeImage(byte[] bytes, string mime, int maxEdge = NormalizeMaxEdge)
    {
        var (w, h) = TryParseDimensions(bytes, mime);
        try
        {
            if (w <= 0 || h <= 0)
            {
                using var probe = System.Drawing.Image.FromStream(new MemoryStream(bytes, writable: false));
                w = probe.Width; h = probe.Height;
            }
            if (w > 0 && h > 0 && System.Math.Max(w, h) <= maxEdge) return (bytes, mime, w, h);
            using var src = System.Drawing.Image.FromStream(new MemoryStream(bytes, writable: false));
            double scale = (double)maxEdge / System.Math.Max(src.Width, src.Height);
            int nw = System.Math.Max(1, (int)(src.Width * scale));
            int nh = System.Math.Max(1, (int)(src.Height * scale));
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                ?? System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()[0];
            using var ms = new MemoryStream();
            using (var ep = new System.Drawing.Imaging.EncoderParameters(1))
            {
                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                bmp.Save(ms, codec, ep);
            }
            return (ms.ToArray(), "image/jpeg", nw, nh);
        }
        catch { return (bytes, mime, w, h); }
    }
    /// <summary>need09-05：缩略图 dataURL（长边≤256 PNG；失败返回 null）。</summary>
    public static string? MakeThumbnailDataUrl(byte[] bytes, int maxEdge = ThumbnailMaxEdge)
    {
        try
        {
            using var src = System.Drawing.Image.FromStream(new MemoryStream(bytes, writable: false));
            double scale = (double)maxEdge / System.Math.Max(src.Width, src.Height);
            int nw = src.Width, nh = src.Height;
            if (scale < 1.0) { nw = System.Math.Max(1, (int)(src.Width * scale)); nh = System.Math.Max(1, (int)(src.Height * scale)); }
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
    public static string ToDataUrl(byte[] bytes, string mime) => "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
}
