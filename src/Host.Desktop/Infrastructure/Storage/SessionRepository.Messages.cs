using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
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
            var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
            store.InsertSingleMessage(workspaceId, sessionId, message);
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
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        var dbMsgs = await store.GetMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        foreach (var m in dbMsgs)
            MarkMissingImages(workspaceId, sessionId, m);
        return dbMsgs;
    }

    public async IAsyncEnumerable<UnifiedChatMessage> StreamMessagesAsync(
        string workspaceId,
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        var dbMsgs = await store.GetMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        foreach (var m in dbMsgs)
        {
            ct.ThrowIfCancellationRequested();
            MarkMissingImages(workspaceId, sessionId, m);
            yield return m;
        }
    }

    public async Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
            await store
                .DeleteSessionPartitionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch { }
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

        // 索引删除同样纳入工作区锁内的读-改-写，避免抹掉其他会话的并发元数据更新。
        await MutateSessionsIndexAsync(
                workspaceId,
                sessions =>
                    sessions.RemoveAll(s =>
                        string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
                    ),
                ct
            )
            .ConfigureAwait(false);
        return true;
    }

    public async Task<HistoryRollbackResult> RollbackCreatedSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        var result = new HistoryRollbackResult();
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));

        try
        {
            await store
                .DeleteSessionPartitionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            using var conn = new SessionHistoryDb(_storageManager).OpenWorkspace(workspaceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM messages WHERE session_id = $sid)"
                + " + (SELECT COUNT(*) FROM turns WHERE session_id = $sid)"
                + " + (SELECT COUNT(*) FROM blocks WHERE session_id = $sid);";
            cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
            long remaining = Convert.ToInt64(cmd.ExecuteScalar());
            result.PartitionDeleted = remaining == 0;
            if (remaining != 0)
                result.Failures.Add(
                    new HistoryRollbackFailure(
                        BranchRollbackStage.Partition,
                        "数据库分区仍有 " + remaining + " 行"
                    )
                );
        }
        catch (Exception ex)
        {
            result.Failures.Add(
                new HistoryRollbackFailure(BranchRollbackStage.Partition, ex.Message)
            );
        }

        try
        {
            string sessionDir = GetSessionDirectory(workspaceId, sessionId ?? string.Empty);
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
            result.DirectoryDeleted = !Directory.Exists(sessionDir);
            if (!result.DirectoryDeleted)
                result.Failures.Add(
                    new HistoryRollbackFailure(BranchRollbackStage.Directory, "会话目录仍存在")
                );
        }
        catch (Exception ex)
        {
            result.Failures.Add(
                new HistoryRollbackFailure(BranchRollbackStage.Directory, ex.Message)
            );
        }

        try
        {
            await MutateSessionsIndexAsync(
                    workspaceId,
                    sessions =>
                        sessions.RemoveAll(s =>
                            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
                        ),
                    ct
                )
                .ConfigureAwait(false);
            result.MetadataDeleted = true;
        }
        catch (Exception ex)
        {
            result.Failures.Add(
                new HistoryRollbackFailure(BranchRollbackStage.Metadata, ex.Message)
            );
        }

        try
        {
            var after = await LoadSessionsIndexAsync(workspaceId, ct).ConfigureAwait(false);
            result.IndexRemoved = !after.Any(s =>
                string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
            );
            if (!result.IndexRemoved)
                result.Failures.Add(
                    new HistoryRollbackFailure(BranchRollbackStage.Index, "会话索引仍包含该会话")
                );
        }
        catch (Exception ex)
        {
            result.Failures.Add(new HistoryRollbackFailure(BranchRollbackStage.Index, ex.Message));
        }

        return result;
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
}
