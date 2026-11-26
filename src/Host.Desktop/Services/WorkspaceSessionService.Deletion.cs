using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed partial class WorkspaceSessionService : IWorkspaceSessionService
{
    private static readonly TimeSpan WorkspaceExclusiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AppendLockTimeout = TimeSpan.FromSeconds(5);

    public async Task<SessionDeletionResult> DeleteSessionTreeAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default,
        Action<string>? cancelSessionTree = null
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return DeletionFailure("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");

        string wsId = workspaceId.Trim();
        string rootId = sessionId.Trim();
        if (
            !SessionDirectoryDeletion.TryResolveWorkspaceDirectory(
                _sessionRepository.Storage,
                wsId,
                out _
            )
            || !SessionDirectoryDeletion.TryResolveSessionDirectory(
                _sessionRepository.Storage,
                wsId,
                rootId,
                out _
            )
        )
            return DeletionFailure("INVALID_SESSION", "工作区 ID 或会话 ID 路径不合法");
        try
        {
            var initialSessions = await _sessionRepository
                .LoadSessionsIndexStrictAsync(wsId, ct)
                .ConfigureAwait(false);
            var tree = await CollectDeletionTreeAsync(wsId, rootId, initialSessions, ct)
                .ConfigureAwait(false);
            if (tree.RootMissing)
                return DeletionNotFound();
            if (tree.ErrorCode != null)
                return DeletionFailure(tree.ErrorCode, "会话树结构无效，未执行删除");

            try
            {
                (cancelSessionTree ?? _cancelSessionTree)?.Invoke(rootId);
            }
            catch (Exception ex)
            {
                return DeletionFailure(
                    "CANCEL_FAILED",
                    $"workspaceId={wsId} sessionId={rootId} stage=Cancel exception={ex.GetType().Name}"
                );
            }

            using var workspaceLease = await _lifecycleGate
                .TryAcquireExclusiveAsync(wsId, WorkspaceExclusiveTimeout, ct)
                .ConfigureAwait(false);
            if (workspaceLease == null)
                return DeletionFailure("WORKSPACE_BUSY", $"workspaceId={wsId} stage=WorkspaceGate");

            // 独占门取得后重新收集，纳入等待期间合法创建的子会话。
            var finalSessions = await _sessionRepository
                .LoadSessionsIndexStrictAsync(wsId, ct)
                .ConfigureAwait(false);
            tree = await CollectDeletionTreeAsync(wsId, rootId, finalSessions, ct)
                .ConfigureAwait(false);
            if (tree.RootMissing)
                return DeletionNotFound();
            if (tree.ErrorCode != null)
                return DeletionFailure(tree.ErrorCode, "会话树结构无效，未执行删除");

            string? busyCode = FindBusyCode(wsId, tree.Nodes, finalSessions);
            if (busyCode != null)
                return DeletionFailure(
                    busyCode,
                    $"workspaceId={wsId} stage=BusyCheck sessions={string.Join(",", tree.Nodes.Select(node => node.SessionId))}"
                );

            if (
                !_operationCoordinator.TryAcquireBatch(
                    wsId,
                    tree.Nodes.Select(node => node.SessionId),
                    SessionOperationKind.History,
                    out var batchLease
                )
            )
            {
                busyCode = FindHolderCode(wsId, tree.Nodes);
                return DeletionFailure(
                    busyCode ?? "SESSION_BUSY",
                    $"workspaceId={wsId} stage=SessionLocks"
                );
            }

            using (batchLease)
            {
                var appendLocks = new List<SemaphoreSlim>();
                try
                {
                    using var appendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    appendTimeout.CancelAfter(AppendLockTimeout);
                    foreach (SessionDeletionNode node in tree.Nodes)
                    {
                        var appendLock = GetSessionAppendLock(node.SessionId);
                        await appendLock.WaitAsync(appendTimeout.Token).ConfigureAwait(false);
                        appendLocks.Add(appendLock);
                    }
                }
                catch (OperationCanceledException)
                {
                    ReleaseAppendLocks(appendLocks);
                    return DeletionFailure(
                        ct.IsCancellationRequested ? "DELETE_CANCELLED" : "SESSION_BUSY",
                        $"workspaceId={wsId} stage=AppendLocks"
                    );
                }

                try
                {
                    WorkspaceBackupInfo backup;
                    try
                    {
                        backup = await new WorkspaceSessionStore(_sessionRepository.Storage)
                            .BackupWorkspaceAsync(wsId, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return DeletionFailure(
                            "BACKUP_FAILED",
                            $"workspaceId={wsId} stage=Backup exception={ex.GetType().Name}",
                            tree.Nodes.Select(node => node.SessionId).ToList()
                        );
                    }

                    string backupFileName = Path.GetFileName(backup.BackupPath);
                    var storageResult = await _sessionRepository
                        .DeleteSessionTreeAsync(wsId, tree.Nodes, ct)
                        .ConfigureAwait(false);
                    var deletedIds = tree.Nodes.Select(node => node.SessionId).ToList();
                    if (!storageResult.DatabaseCommitted)
                    {
                        return new SessionDeletionResult
                        {
                            Outcome = SessionDeletionOutcome.Failed,
                            ErrorCode = storageResult.ErrorCode ?? "SESSION_DATABASE_DELETE_FAILED",
                            DestructiveCommitCompleted = false,
                            BackupFileName = backupFileName,
                            DeletedSessionIds = Array.Empty<string>(),
                            ResidualSessionIds = deletedIds,
                            Database = SessionDeletionStage.Failed,
                            Directories = SessionDeletionStage.NotRequested,
                            Index = SessionDeletionStage.NotRequested,
                            Warnings = storageResult.Warnings,
                        };
                    }

                    bool partial =
                        storageResult.Directories != SessionDeletionStage.Completed
                        || storageResult.Index != SessionDeletionStage.Completed
                        || storageResult.ResidualSessionIds.Count > 0;
                    LogDeletion(
                        $"workspaceId={wsId} root={rootId} sessions={string.Join(",", deletedIds)} outcome={(partial ? "partial" : "deleted")} backup={backupFileName}"
                    );
                    return new SessionDeletionResult
                    {
                        Outcome = partial
                            ? SessionDeletionOutcome.Partial
                            : SessionDeletionOutcome.Deleted,
                        ErrorCode = partial ? "SESSION_DELETION_PARTIAL" : null,
                        DestructiveCommitCompleted = true,
                        BackupFileName = backupFileName,
                        DeletedSessionIds = deletedIds,
                        ResidualSessionIds = storageResult.ResidualSessionIds,
                        Database = SessionDeletionStage.Completed,
                        Directories = storageResult.Directories,
                        Index = storageResult.Index,
                        Warnings = storageResult.Warnings,
                    };
                }
                finally
                {
                    ReleaseAppendLocks(appendLocks);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return DeletionFailure("DELETE_CANCELLED", $"workspaceId={wsId} sessionId={rootId}");
        }
        catch (Exception ex)
        {
            return DeletionFailure(
                "SESSION_DELETE_ERROR",
                $"workspaceId={wsId} sessionId={rootId} exception={ex.GetType().Name}"
            );
        }
    }

    private async Task<SessionDeletionTreeResult> CollectDeletionTreeAsync(
        string workspaceId,
        string rootId,
        IReadOnlyList<SessionMetadata> sessions,
        CancellationToken ct
    )
    {
        var tree = SessionDeletionTree.Collect(rootId, sessions);
        if (!tree.RootMissing)
            return tree;

        // 目录阶段失败后索引可能已移除；按残留 ID 重试时仍允许清理磁盘残留。
        var rootMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, rootId, ct)
            .ConfigureAwait(false);
        bool directoryExists = Directory.Exists(
            _sessionRepository.GetSessionDirectory(workspaceId, rootId)
        );
        bool orphanedChildren = sessions.Any(session =>
            string.Equals(session.ParentSessionId, rootId, StringComparison.OrdinalIgnoreCase)
        );
        if (rootMeta == null && !directoryExists && !orphanedChildren)
            return tree;

        var candidates = sessions.ToList();
        if (
            !candidates.Any(session =>
                string.Equals(session.Id, rootId, StringComparison.OrdinalIgnoreCase)
            )
        )
            candidates.Insert(0, new SessionMetadata { Id = rootId });
        return SessionDeletionTree.Collect(rootId, candidates);
    }

    private string? FindBusyCode(
        string workspaceId,
        IReadOnlyList<SessionDeletionNode> nodes,
        IReadOnlyList<SessionMetadata> sessions
    )
    {
        var targetIds = new HashSet<string>(
            nodes.Select(node => node.SessionId),
            StringComparer.OrdinalIgnoreCase
        );
        if (
            sessions.Any(session =>
                targetIds.Contains(session.Id)
                && string.Equals(session.SubStatus, "running", StringComparison.OrdinalIgnoreCase)
            )
        )
            return "SESSION_BUSY";
        return FindHolderCode(workspaceId, nodes);
    }

    private string? FindHolderCode(string workspaceId, IReadOnlyList<SessionDeletionNode> nodes)
    {
        bool anyHeld = false;
        bool historyHeld = false;
        foreach (SessionDeletionNode node in nodes)
        {
            if (!_operationCoordinator.IsHeld(workspaceId, node.SessionId))
                continue;
            anyHeld = true;
            if (
                _operationCoordinator.TryGetHolderKind(workspaceId, node.SessionId, out var kind)
                && kind == SessionOperationKind.History
            )
                historyHeld = true;
        }
        if (!anyHeld)
            return null;
        return historyHeld ? "HISTORY_OPERATION_IN_PROGRESS" : "SESSION_BUSY";
    }

    private static void ReleaseAppendLocks(IEnumerable<SemaphoreSlim> locks)
    {
        foreach (SemaphoreSlim appendLock in locks)
            appendLock.Release();
    }

    private static SessionDeletionResult DeletionNotFound() =>
        new()
        {
            Outcome = SessionDeletionOutcome.NotFound,
            ErrorCode = "SESSION_NOT_FOUND",
            DestructiveCommitCompleted = false,
            Database = SessionDeletionStage.NotRequested,
            Directories = SessionDeletionStage.NotRequested,
            Index = SessionDeletionStage.NotRequested,
        };

    private static SessionDeletionResult DeletionFailure(
        string code,
        string? warning = null,
        IReadOnlyList<string>? residualIds = null
    )
    {
        if (!string.IsNullOrWhiteSpace(warning))
            LogDeletion(warning);
        return new SessionDeletionResult
        {
            Outcome = SessionDeletionOutcome.Failed,
            ErrorCode = code,
            DestructiveCommitCompleted = false,
            ResidualSessionIds = residualIds ?? Array.Empty<string>(),
            Database = SessionDeletionStage.NotRequested,
            Directories = SessionDeletionStage.NotRequested,
            Index = SessionDeletionStage.NotRequested,
            Warnings = warning == null ? Array.Empty<string>() : new[] { warning },
        };
    }

    private static void LogDeletion(string message)
    {
        try
        {
            HaloLogger.App.Info("[SessionDeletion] " + message);
        }
        catch
        {
            // 日志失败不得改变删除阶段结果。
        }
    }
}
