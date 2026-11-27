using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>
/// 工作区历史库空间治理领域服务：手动压实与备份清理的唯一编排入口。
/// 忙态检查在独占门之外再做一次会话级判定；权威写失败向上传播，辅助阶段只记告警。
/// </summary>
public sealed class WorkspaceStorageMaintenanceService
{
    private readonly StoragePathManager _storage;
    private readonly ISessionRepository _sessions;
    private readonly SessionOperationCoordinator _coordinator;
    private readonly WorkspaceLifecycleGate _gate;
    private readonly WorkspaceDatabaseMaintenance _maintenance;

    public WorkspaceStorageMaintenanceService(
        StoragePathManager storage,
        ISessionRepository? sessions = null,
        SessionOperationCoordinator? coordinator = null,
        WorkspaceLifecycleGate? gate = null
    )
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _sessions = sessions ?? new SessionRepository(_storage);
        _coordinator = coordinator ?? SessionOperationCoordinator.Shared;
        _gate = gate ?? WorkspaceLifecycleGate.Shared;
        _maintenance = new WorkspaceDatabaseMaintenance(_storage);
    }

    public async Task<WorkspaceCompactionResult> CompactWorkspaceHistoryAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
            return FailCompact("INVALID_WORKSPACE_ID", "workspaceId 不能为空");

        if (!SessionDirectoryDeletion.TryResolveWorkspaceDirectory(_storage, wsId, out _))
            return FailCompact("INVALID_WORKSPACE_ID", $"workspaceId={wsId} stage=PathValidation");

        string dbPath = _storage.GetWorkspaceDbPath(wsId);
        if (!File.Exists(dbPath))
            return FailCompact("DATABASE_NOT_FOUND", $"workspaceId={wsId} stage=DatabaseMissing");

        ct.ThrowIfCancellationRequested();
        var busy = await FindWorkspaceBusyCodeAsync(wsId, ct).ConfigureAwait(false);
        if (busy != null)
            return FailCompact(busy, $"workspaceId={wsId} stage=BusyCheck code={busy}");

        var result = await _maintenance.CompactAsync(wsId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            LogMaintenance(
                $"workspaceId={wsId} stage=Compact result={result.ErrorCode} before={result.BeforeBytes} after={result.AfterBytes}"
            );
        }
        else
        {
            LogMaintenance(
                $"workspaceId={wsId} stage=Compact result=ok before={result.BeforeBytes} after={result.AfterBytes} reclaimed={result.ReclaimedBytes}"
            );
        }

        return result;
    }

    public async Task<WorkspaceBackupCleanupResult> DeleteWorkspaceBackupAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        string wsId = (workspaceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsId))
            return FailCleanup("INVALID_WORKSPACE_ID", "workspaceId 不能为空");

        if (!SessionDirectoryDeletion.TryResolveWorkspaceDirectory(_storage, wsId, out _))
            return FailCleanup("INVALID_WORKSPACE_ID", $"workspaceId={wsId} stage=PathValidation");

        ct.ThrowIfCancellationRequested();
        var busy = await FindWorkspaceBusyCodeAsync(wsId, ct).ConfigureAwait(false);
        if (busy != null)
            return FailCleanup(busy, $"workspaceId={wsId} stage=BusyCheck code={busy}");

        var result = await _maintenance.DeleteBackupAsync(wsId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            LogMaintenance($"workspaceId={wsId} stage=DeleteBackup result={result.ErrorCode}");
        }
        else
        {
            LogMaintenance(
                $"workspaceId={wsId} stage=DeleteBackup result=ok existed={result.BackupExisted} freed={result.FreedBytes}"
            );
        }

        return result;
    }

    private async Task<string?> FindWorkspaceBusyCodeAsync(string workspaceId, CancellationToken ct)
    {
        try
        {
            if (WorkspaceLifecycleGate.Shared.IsExclusiveHeld(workspaceId))
                return "WORKSPACE_BUSY";
        }
        catch { }

        IReadOnlyList<SessionMetadata> sessions;
        try
        {
            sessions = await _sessions
                .LoadSessionsIndexAsync(workspaceId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // 索引损坏不阻断维护忙态判定：退化为无会话忙态，由底层维护门继续保护。
            return null;
        }

        foreach (var session in sessions)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                continue;
            if (string.Equals(session.SubStatus, "running", StringComparison.OrdinalIgnoreCase))
                return "SESSION_BUSY";

            try
            {
                if (_coordinator.IsHeld(workspaceId, session.Id))
                    return
                        _coordinator.TryGetHolderKind(workspaceId, session.Id, out var kind)
                        && kind == SessionOperationKind.History
                        ? "HISTORY_OPERATION_IN_PROGRESS"
                        : "SESSION_BUSY";
            }
            catch { }
        }

        return _gate.IsExclusiveHeld(workspaceId) ? "WORKSPACE_BUSY" : null;
    }

    private static WorkspaceCompactionResult FailCompact(string code, string warning) =>
        FailCompactWithLog(code, warning);

    private static WorkspaceCompactionResult FailCompactWithLog(string code, string warning)
    {
        LogMaintenance(warning);
        return new WorkspaceCompactionResult { Success = false, ErrorCode = code };
    }

    private static WorkspaceBackupCleanupResult FailCleanup(string code, string warning)
    {
        LogMaintenance(warning);
        return new WorkspaceBackupCleanupResult { Success = false, ErrorCode = code };
    }

    private static void LogMaintenance(string message)
    {
        try
        {
            HaloLogger.App.Warn("[WorkspaceMaintenance] " + message);
        }
        catch
        {
            // 日志失败不得改变维护阶段结果。
        }
    }
}
