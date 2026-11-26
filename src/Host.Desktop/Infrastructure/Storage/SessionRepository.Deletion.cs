using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

public sealed partial class SessionRepository : ISessionRepository
{
    public async Task<SessionDeletionStorageResult> DeleteSessionTreeAsync(
        string workspaceId,
        IReadOnlyList<SessionDeletionNode> nodes,
        CancellationToken ct = default
    )
    {
        var stableNodes = nodes
            .Where(node => node != null && !string.IsNullOrWhiteSpace(node.SessionId))
            .Select(node => new SessionDeletionNode
            {
                SessionId = node.SessionId.Trim(),
                Depth = node.Depth,
            })
            .GroupBy(node => node.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(node => node.Depth).First())
            .OrderBy(node => node.SessionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.SessionId, StringComparer.Ordinal)
            .ToList();
        if (stableNodes.Count == 0)
        {
            return new SessionDeletionStorageResult
            {
                DatabaseCommitted = true,
                Database = SessionDeletionStage.Completed,
                Directories = SessionDeletionStage.Completed,
                Index = SessionDeletionStage.Completed,
            };
        }

        var ids = stableNodes.Select(node => node.SessionId).ToList();
        if (
            !SessionDirectoryDeletion.TryResolveWorkspaceDirectory(
                _storageManager,
                workspaceId,
                out _
            )
        )
        {
            return StorageFailure(
                SessionDeletionStage.Failed,
                "INVALID_WORKSPACE_ID",
                ids,
                "workspace path is outside the managed sessions directory"
            );
        }

        var resolvedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sessionId in ids)
        {
            if (
                !SessionDirectoryDeletion.TryResolveSessionDirectory(
                    _storageManager,
                    workspaceId,
                    sessionId,
                    out string directory
                )
            )
            {
                return StorageFailure(
                    SessionDeletionStage.Failed,
                    "INVALID_SESSION_PATH",
                    ids,
                    $"sessionId={sessionId} stage=PathValidation"
                );
            }
            resolvedDirectories[sessionId] = directory;
        }

        try
        {
            var store = new WorkspaceSessionStore(_storageManager, this);
            await store.DeleteSessionPartitionsAsync(workspaceId, ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string code =
                ex is OperationCanceledException
                    ? "DELETE_CANCELLED"
                    : "SESSION_DATABASE_DELETE_FAILED";
            return StorageFailure(
                SessionDeletionStage.Failed,
                code,
                ids,
                $"workspaceId={workspaceId} stage=Database exception={ex.GetType().Name}"
            );
        }

        var warnings = new List<string>();
        var residual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            SessionDeletionNode node in stableNodes
                .OrderByDescending(item => item.Depth)
                .ThenBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SessionId, StringComparer.Ordinal)
        )
        {
            string sessionId = node.SessionId;
            string directory = resolvedDirectories[sessionId];
            try
            {
                if (SessionDirectoryDeletion.ContainsReparsePoint(directory, ct))
                {
                    residual.Add(sessionId);
                    warnings.Add(
                        $"REPARSE_POINT_SKIPPED workspaceId={workspaceId} sessionId={sessionId} path={directory}"
                    );
                    LogDeletionWarning(warnings[^1]);
                    continue;
                }
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                if (Directory.Exists(directory))
                {
                    residual.Add(sessionId);
                    warnings.Add(
                        $"DIRECTORY_RESIDUAL workspaceId={workspaceId} sessionId={sessionId} path={directory}"
                    );
                    LogDeletionWarning(warnings[^1]);
                }
            }
            catch (Exception ex)
            {
                residual.Add(sessionId);
                warnings.Add(
                    $"DIRECTORY_DELETE_FAILED workspaceId={workspaceId} sessionId={sessionId} path={directory} exception={ex.GetType().Name}"
                );
                LogDeletionWarning(warnings[^1]);
            }
        }

        var directoriesStage =
            residual.Count == 0 ? SessionDeletionStage.Completed : SessionDeletionStage.Failed;
        var indexStage = SessionDeletionStage.Completed;
        var targetIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        try
        {
            await RemoveSessionsFromIndexStrictAsync(workspaceId, targetIds, ct)
                .ConfigureAwait(false);
            var remaining = await LoadSessionsIndexStrictAsync(workspaceId, ct)
                .ConfigureAwait(false);
            var remainingIds = remaining
                .Where(item => targetIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToList();
            if (remainingIds.Count > 0)
            {
                indexStage = SessionDeletionStage.Failed;
                foreach (string sessionId in remainingIds)
                    residual.Add(sessionId);
                warnings.Add(
                    $"INDEX_VERIFICATION_FAILED workspaceId={workspaceId} residual={string.Join(",", remainingIds)}"
                );
                LogDeletionWarning(warnings[^1]);
            }
        }
        catch (Exception ex)
        {
            indexStage = SessionDeletionStage.Failed;
            foreach (string sessionId in ids)
                residual.Add(sessionId);
            warnings.Add(
                $"INDEX_DELETE_FAILED workspaceId={workspaceId} exception={ex.GetType().Name}"
            );
            LogDeletionWarning(warnings[^1]);
        }

        return new SessionDeletionStorageResult
        {
            DatabaseCommitted = true,
            Database = SessionDeletionStage.Completed,
            Directories = directoriesStage,
            Index = indexStage,
            ResidualSessionIds = residual
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToList(),
            Warnings = warnings,
        };
    }

    private async Task RemoveSessionsFromIndexStrictAsync(
        string workspaceId,
        HashSet<string> targetIds,
        CancellationToken ct
    )
    {
        var indexLock = GetSessionsIndexLock(workspaceId);
        await indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessions = await LoadSessionsIndexStrictAsync(workspaceId, ct)
                .ConfigureAwait(false);
            sessions.RemoveAll(item => targetIds.Contains(item.Id));
            await AtomicFileWriter
                .WriteJsonAtomicAsync(GetSessionsIndexPath(workspaceId), sessions, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            indexLock.Release();
        }
    }

    private static SessionDeletionStorageResult StorageFailure(
        SessionDeletionStage databaseStage,
        string code,
        IReadOnlyList<string> ids,
        string warning
    )
    {
        LogDeletionWarning(warning);
        return new SessionDeletionStorageResult
        {
            DatabaseCommitted = false,
            ErrorCode = code,
            Database = databaseStage,
            Directories = SessionDeletionStage.NotRequested,
            Index = SessionDeletionStage.NotRequested,
            ResidualSessionIds = ids,
            Warnings = new[] { warning },
        };
    }

    private static void LogDeletionWarning(string warning)
    {
        try
        {
            HaloLogger.App.Warn("[SessionDeletion] " + warning);
        }
        catch
        {
            // 日志失败不得改变删除阶段结果。
        }
    }
}

internal static class SessionDirectoryDeletion
{
    public static bool TryResolveWorkspaceDirectory(
        StoragePathManager storage,
        string workspaceId,
        out string directory
    ) => TryResolveChildDirectory(storage.SessionsDirectory, workspaceId, out directory);

    public static bool TryResolveSessionDirectory(
        StoragePathManager storage,
        string workspaceId,
        string sessionId,
        out string directory
    )
    {
        if (!TryResolveWorkspaceDirectory(storage, workspaceId, out string workspaceDirectory))
        {
            directory = string.Empty;
            return false;
        }
        return TryResolveChildDirectory(workspaceDirectory, sessionId, out directory);
    }

    private static bool TryResolveChildDirectory(
        string parent,
        string? segment,
        out string directory
    )
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(segment))
            return false;
        string value = segment.Trim();
        if (
            value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/')
            || value.Contains('\\')
            || Path.IsPathRooted(value)
        )
            return false;

        string parentFull = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(parentFull, value));
        string prefix = parentFull + Path.DirectorySeparatorChar;
        if (
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, parentFull, StringComparison.OrdinalIgnoreCase)
        )
            return false;
        directory = candidate;
        return true;
    }

    public static bool ContainsReparsePoint(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return false;
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = pending.Dequeue();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (Directory.Exists(entry))
                    pending.Enqueue(entry);
            }
        }
        return false;
    }
}
