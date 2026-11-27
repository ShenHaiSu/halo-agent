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

public sealed partial class WorkspaceSessionService
{
    private static readonly string[] EmptyPaths = Array.Empty<string>();

    /// <summary>
    /// 显式双模式移除：独占门内先做权威校验与取消，再原子改索引，最后执行物理阶段。
    /// 索引提交后物理失败只报 CompletedWithWarnings，不再把工作区“复活”到列表里。
    /// </summary>
    public async Task<WorkspaceRemovalResult> RemoveWorkspaceV2Async(
        string workspaceId,
        WorkspaceRemovalMode mode,
        CancellationToken ct = default,
        Action<string>? cancelWorkspaceSessions = null
    )
    {
        if (
            !SessionDirectoryDeletion.TryResolveWorkspaceDirectory(
                _sessionRepository.Storage,
                workspaceId,
                out string sessionRoot
            )
        )
            return Failure(mode, "INVALID_WORKSPACE_ID");

        string wsId = workspaceId.Trim();
        try
        {
            using var workspaceLease = await _lifecycleGate
                .TryAcquireExclusiveAsync(wsId, WorkspaceExclusiveTimeout, ct)
                .ConfigureAwait(false);
            if (workspaceLease == null)
                return Failure(mode, "WORKSPACE_BUSY");

            var workspaces = await _sessionRepository
                .LoadWorkspacesIndexAsync(ct)
                .ConfigureAwait(false);
            var workspace = workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, wsId, StringComparison.OrdinalIgnoreCase)
            );
            if (workspace == null)
                return NotFound(mode);

            string projectRoot;
            string haloRoot;
            try
            {
                haloRoot = SkillMemoryService.ResolveProjectRoot(workspace.RootPath);
                projectRoot = Path.GetDirectoryName(haloRoot) ?? workspace.RootPath;
            }
            catch (Exception)
            {
                return Failure(mode, "PATH_TRAVERSAL");
            }

            if (mode == WorkspaceRemovalMode.Purge)
            {
                List<SessionMetadata> sessions;
                try
                {
                    sessions = await _sessionRepository
                        .LoadSessionsIndexStrictAsync(wsId, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return Failure(mode, "SESSIONS_INDEX_INVALID");
                }

                try
                {
                    foreach (var session in sessions)
                        (cancelWorkspaceSessions ?? _cancelSessionTree)?.Invoke(session.Id);
                }
                catch (Exception)
                {
                    return Failure(mode, "CANCEL_FAILED", $"workspaceId={wsId} stage=Cancel");
                }

                if (
                    SafeDirectoryDelete.ContainsReparsePoint(sessionRoot, ct)
                    || SafeDirectoryDelete.ContainsReparsePoint(haloRoot, ct)
                )
                    return Failure(
                        mode,
                        "REPARSE_POINT_UNSUPPORTED",
                        $"workspaceId={wsId} stage=PathPrecheck"
                    );
            }

            WorkspaceInfo? removed;
            try
            {
                removed = await _sessionRepository
                    .MutateWorkspacesIndexAsync(
                        list =>
                        {
                            var target = list.FirstOrDefault(w =>
                                string.Equals(w.Id, wsId, StringComparison.OrdinalIgnoreCase)
                            );
                            if (target == null)
                                return null;
                            list.Remove(target);
                            return target;
                        },
                        ct
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Failure(mode, "INDEX_REMOVE_FAILED", $"workspaceId={wsId} stage=Index");
            }
            if (removed == null)
                return NotFound(mode);

            LogRemoval(
                $"workspaceId={wsId} mode={WorkspaceRemovalModeParser.ToWire(mode)} stage=IndexCommitted"
            );

            return mode == WorkspaceRemovalMode.Unbind
                ? UnbindPhysicalStages(removed, ct)
                : PurgePhysicalStages(removed, sessionRoot, projectRoot, ct);
        }
        catch (OperationCanceledException)
        {
            return Failure(mode, "DELETE_CANCELLED");
        }
        catch (Exception ex)
        {
            return Failure(
                mode,
                "WORKSPACE_REMOVAL_ERROR",
                $"workspaceId={wsId} exception={ex.GetType().Name}"
            );
        }
    }

    private static WorkspaceRemovalResult UnbindPhysicalStages(
        WorkspaceInfo workspace,
        CancellationToken ct
    )
    {
        var inspection = SkillMemoryService.InspectProjectDomain(workspace.RootPath);
        if (!inspection.SafeEmpty)
        {
            string reason = inspection.Reason ?? ProjectDomainReasons.InspectionFailed;
            LogRemoval(
                $"workspaceId={workspace.Id} stage=HaloAgent reason={reason} outcome=Retained"
            );
            return new WorkspaceRemovalResult
            {
                Outcome = WorkspaceRemovalOutcome.CompletedWithWarnings,
                Mode = WorkspaceRemovalMode.Unbind,
                WorkspaceUnbound = true,
                SessionData = WorkspaceResourceStatus.NotRequested,
                HaloAgent = WorkspaceResourceStatus.Retained,
                HaloAgentReason = reason,
                Warnings = new[] { "HALO_AGENT_RETAINED" },
            };
        }

        if (
            !SafeDirectoryDelete.TryDeleteEmptyProjectDomain(
                workspace.RootPath,
                ct,
                out var failedPaths
            )
        )
        {
            LogRemoval(
                $"workspaceId={workspace.Id} stage=HaloAgent outcome=Failed paths={string.Join(",", failedPaths)}"
            );
            return new WorkspaceRemovalResult
            {
                Outcome = WorkspaceRemovalOutcome.CompletedWithWarnings,
                Mode = WorkspaceRemovalMode.Unbind,
                WorkspaceUnbound = true,
                SessionData = WorkspaceResourceStatus.NotRequested,
                HaloAgent = WorkspaceResourceStatus.Failed,
                HaloAgentReason = "CLEANUP_FAILED",
                FailedPaths = failedPaths,
                Warnings = new[] { "HALO_AGENT_CLEANUP_FAILED" },
            };
        }

        return new WorkspaceRemovalResult
        {
            Outcome = WorkspaceRemovalOutcome.Completed,
            Mode = WorkspaceRemovalMode.Unbind,
            WorkspaceUnbound = true,
            SessionData = WorkspaceResourceStatus.NotRequested,
            HaloAgent = WorkspaceResourceStatus.Deleted,
            HaloAgentReason = inspection.Reason ?? ProjectDomainReasons.Empty,
        };
    }

    private static WorkspaceRemovalResult PurgePhysicalStages(
        WorkspaceInfo workspace,
        string sessionRoot,
        string projectRoot,
        CancellationToken ct
    )
    {
        var failedPaths = new List<string>();
        var warnings = new List<string>();

        string sessionParent = Path.GetDirectoryName(sessionRoot) ?? sessionRoot;
        bool sessionDeleted = SafeDirectoryDelete.TryDeleteChild(
            sessionParent,
            Path.GetFileName(sessionRoot),
            recursive: true,
            ct,
            out var sessionFailures
        );
        if (!sessionDeleted)
        {
            warnings.Add("SESSION_DELETE_FAILED");
            failedPaths.AddRange(sessionFailures);
        }

        bool haloDeleted = SafeDirectoryDelete.TryDeleteChild(
            projectRoot,
            ".haloagent",
            recursive: true,
            ct,
            out var haloFailures
        );
        if (!haloDeleted)
        {
            warnings.Add("HALO_AGENT_DELETE_FAILED");
            failedPaths.AddRange(haloFailures);
        }

        LogRemoval(
            $"workspaceId={workspace.Id} mode=purge stage=Physical sessions={(sessionDeleted ? "deleted" : "failed")} halo={(haloDeleted ? "deleted" : "failed")}"
        );
        return new WorkspaceRemovalResult
        {
            Outcome =
                warnings.Count == 0
                    ? WorkspaceRemovalOutcome.Completed
                    : WorkspaceRemovalOutcome.CompletedWithWarnings,
            Mode = WorkspaceRemovalMode.Purge,
            WorkspaceUnbound = true,
            SessionData = sessionDeleted
                ? WorkspaceResourceStatus.Deleted
                : WorkspaceResourceStatus.Failed,
            HaloAgent = haloDeleted
                ? WorkspaceResourceStatus.Deleted
                : WorkspaceResourceStatus.Failed,
            HaloAgentReason = haloDeleted ? ProjectDomainReasons.Empty : "DELETE_FAILED",
            FailedPaths = failedPaths,
            Warnings = warnings,
        };
    }

    private static WorkspaceRemovalResult NotFound(WorkspaceRemovalMode mode) =>
        new()
        {
            Outcome = WorkspaceRemovalOutcome.NotFound,
            Mode = mode,
            ErrorCode = "WORKSPACE_NOT_FOUND",
            SessionData = WorkspaceResourceStatus.NotRequested,
            HaloAgent = WorkspaceResourceStatus.NotRequested,
        };

    private static WorkspaceRemovalResult Failure(
        WorkspaceRemovalMode mode,
        string code,
        string? warning = null
    )
    {
        if (!string.IsNullOrEmpty(warning))
            LogRemoval(warning);
        return new WorkspaceRemovalResult
        {
            Outcome = WorkspaceRemovalOutcome.Failed,
            Mode = mode,
            ErrorCode = code,
            SessionData = WorkspaceResourceStatus.NotRequested,
            HaloAgent = WorkspaceResourceStatus.NotRequested,
            Warnings = warning == null ? EmptyPaths : new[] { warning },
        };
    }

    private static void LogRemoval(string message)
    {
        try
        {
            HaloLogger.App.Info("[WorkspaceRemoval] " + message);
        }
        catch
        {
            // 日志失败不得改变移除阶段结果。
        }
    }
}
