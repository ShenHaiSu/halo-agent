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
    string GetWorkspaceDirectory(string workspaceId);
    string GetSessionDirectory(string workspaceId, string sessionId);
    string GetMessagesJsonlPath(string workspaceId, string sessionId);
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
            return list ?? new List<SessionMetadata>();
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

        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SessionMetadata>(json, HaloJsonOptions.IndentedCamelCase);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"Failed to load session meta for session '{sessionId}' in ws '{workspaceId}'", ex);
            return null;
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
        sb.AppendLine($"- **Total Tokens**: In {meta?.TotalInputTokens ?? 0} / Out {meta?.TotalOutputTokens ?? 0}");
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
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
