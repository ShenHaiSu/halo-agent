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

public sealed partial class SessionRepository : ISessionRepository
{
    public async Task AppendMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    )
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
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

            string line =
                JsonSerializer.Serialize(message, HaloJsonOptions.CompactCamelCase) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);

            await using var fs = new FileStream(
                jsonlPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true
            );
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<List<UnifiedChatMessage>> GetMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
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
            using var fs = new FileStream(
                jsonlPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var msg = JsonSerializer.Deserialize<UnifiedChatMessage>(
                        line,
                        HaloJsonOptions.CompactCamelCase
                    );
                    if (msg != null)
                    {
                        MarkMissingImages(workspaceId, sessionId, msg);
                        list.Add(msg);
                    }
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn(
                        $"Failed to deserialize message line in session '{sessionId}': {ex.Message}"
                    );
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
        [EnumeratorCancellation] CancellationToken ct = default
    )
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
            using var fs = new FileStream(
                jsonlPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                UnifiedChatMessage? msg = null;
                try
                {
                    msg = JsonSerializer.Deserialize<UnifiedChatMessage>(
                        line,
                        HaloJsonOptions.CompactCamelCase
                    );
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn(
                        $"Failed to deserialize message line in session '{sessionId}': {ex.Message}"
                    );
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

    public async Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
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
                HaloLogger.App.Warn(
                    $"Failed to delete session directory '{sessionDir}': {ex.Message}"
                );
            }
        }

        var sessions = await LoadSessionsIndexAsync(workspaceId, ct).ConfigureAwait(false);
        int removed = sessions.RemoveAll(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (removed > 0)
        {
            await SaveSessionsIndexAsync(workspaceId, sessions, ct).ConfigureAwait(false);
        }
        return true;
    }

    public Task<bool> DeleteWorkspaceDirectoryAsync(
        string workspaceId,
        CancellationToken ct = default
    )
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
                HaloLogger.App.Warn(
                    $"Failed to delete workspace directory '{wsDir}': {ex.Message}"
                );
                return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// need05-03：备份 messages.jsonl → messages.compact-bak-{UTC:yyyyMMddHHmmss}.jsonl，返回备份文件名。
    /// 与 AppendMessageAsync 持同一把 _sessionLocks[sessionId] 锁，天然互斥。
    /// </summary>
    public Task<string> BackupMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.compact-bak-", ct);

    /// <summary>need07：前缀可配备份（replay-bak / compact-bak 共用同一原子拷贝路径）。</summary>
    public async Task<string> BackupMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        string filePrefix,
        CancellationToken ct = default
    )
    {
        string prefix = string.IsNullOrWhiteSpace(filePrefix)
            ? "messages.compact-bak-"
            : filePrefix.Trim();
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        string backupFileName = $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl";
        string backupPath = Path.Combine(
            GetSessionDirectory(workspaceId, sessionId),
            backupFileName
        );
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(jsonlPath))
            {
                using var src = new FileStream(
                    jsonlPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                using var dst = new FileStream(
                    backupPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                );
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(backupPath, string.Empty, Encoding.UTF8, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            sessionLock.Release();
        }
        return backupFileName;
    }
}
