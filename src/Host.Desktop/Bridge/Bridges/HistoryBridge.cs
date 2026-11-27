using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>历史右键操作的 Bridge 入口：能力、错误映射与会话操作互斥在此闭合。</summary>
public sealed partial class HostBridgeFacade
{
    private static readonly Dictionary<string, string> HistoryErrorMessages =
        new(StringComparer.Ordinal)
        {
            ["ANCHOR_NOT_FOUND"] = "历史已变化，未找到对应消息，请刷新后重试",
            ["INVALID_ANCHOR_ROLE"] = "历史消息角色不支持此操作",
            ["INVALID_BRANCH_REQUEST"] = "分支参数不合法，请刷新菜单后重试",
            ["SESSION_BUSY"] = "当前会话正在生成或压缩，请稍后再试",
            ["HISTORY_OPERATION_IN_PROGRESS"] = "当前会话正在执行其他历史操作",
            ["SUB_READONLY"] = "子会话历史只读，不可修改",
            ["HISTORY_READONLY"] = "历史版本只读，不可重放，可分支",
            ["EMPTY_REPLAY_TEXT"] = "该消息没有可重放的文本",
            ["NO_PRIOR_USER"] = "未找到该回复所属的用户消息",
            ["REWRITE_ERROR"] = "历史重写未完成，请刷新确认并检查备份",
            ["TRUNCATE_COMMIT_ERROR"] = "历史截断提交状态不确定，请勿重复操作",
            ["BRANCH_COPY_ERROR"] = "分支复制失败，已回滚，原会话未改动",
            ["BRANCH_ROLLBACK_FAILED"] = "分支清理失败，存在残留会话，请手动处理",
            ["BACKUP_FAILED"] = "删除前备份失败，未删除任何数据，请检查磁盘或权限",
            ["DELETE_COMMIT_FAILED"] = "整轮删除提交失败，已回滚，请刷新后重试",
            ["DELETE_COMMIT_UNKNOWN"] = "提交状态不确定，请勿重复删除，请刷新对账",
            ["SESSION_NOT_FOUND"] = "会话不存在或已被删除"
        };

    internal static string HistoryErrorMessage(string? code, string fallback) =>
        !string.IsNullOrWhiteSpace(code) && HistoryErrorMessages.TryGetValue(code, out var message)
            ? message
            : fallback;

    private SessionOperationLease? AcquireHistoryLock(
        string workspaceId,
        string sessionId,
        out string? errorCode
    )
    {
        if (
            _sessionOperations.TryAcquire(
                workspaceId,
                sessionId,
                SessionOperationKind.History,
                TimeSpan.FromSeconds(5),
                out var lease,
                out var waitMs
            )
        )
        {
            errorCode = null;
            return lease;
        }
        errorCode =
            _sessionOperations.TryGetHolderKind(workspaceId, sessionId, out var holder)
            && holder == SessionOperationKind.History
                ? "HISTORY_OPERATION_IN_PROGRESS"
                : "SESSION_BUSY";
        try
        {
            HaloLogger.App.Info(
                "[SessionLock] conflict ws="
                    + workspaceId
                    + " sess="
                    + sessionId
                    + " kind=History"
                    + " waitMs="
                    + waitMs
            );
        }
        catch { }
        return null;
    }

    private async Task<bool> IsSubSessionAsync(string workspaceId, string sessionId)
    {
        try
        {
            var meta = await _workspaceSessionService
                .GetSessionAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            return IsSubReadOnly(meta);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHistoryReadonly(SessionMetadata? meta) => false;

    private Task<bool> IsHistoryReadonlyAsync(string workspaceId, string sessionId) =>
        Task.FromResult(false);

    /// <summary>只读能力探测：不获取历史写锁，不修改数据。</summary>
    public Task<BridgeResponse<HistoryCapabilities>> GetHistoryCapabilities(
        string workspaceId,
        string sessionId
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(
                BridgeResponse<HistoryCapabilities>.Fail("INVALID_SESSION", "工作区或会话不能为空")
            );
        return Task.FromResult(
            BridgeResponse<HistoryCapabilities>.Success(HistoryCapabilities.Supported)
        );
    }

    public async Task<BridgeResponse<HistoryTruncateResult>> TruncateSessionHistory(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        bool removeAnchor
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
                return BridgeResponse<HistoryTruncateResult>.Fail("INVALID_SESSION", "工作区或会话不能为空");
            if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    "SUB_READONLY",
                    HistoryErrorMessage("SUB_READONLY", "子会话不可操作")
                );
            if (await IsHistoryReadonlyAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    "HISTORY_READONLY",
                    HistoryErrorMessage("HISTORY_READONLY", "历史只读")
                );
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );

            bool handedOff = false;
            try
            {
                var result = await _workspaceSessionService
                    .TruncateSessionHistoryAsync(
                        workspaceId,
                        sessionId,
                        anchorMessageId,
                        removeAnchor,
                        default,
                        lease
                    )
                    .ConfigureAwait(false);
                handedOff = true;
                if (!result.Success)
                    return BridgeResponse<HistoryTruncateResult>.Fail(
                        result.ErrorCode ?? "TRUNCATE_FAILED",
                        HistoryErrorMessage(result.ErrorCode, "截断失败")
                    );
                return BridgeResponse<HistoryTruncateResult>.Success(result);
            }
            catch (TruncateCommitException ex)
            {
                handedOff = true;
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    "TRUNCATE_COMMIT_ERROR",
                    HistoryErrorMessage("TRUNCATE_COMMIT_ERROR", ex.Message)
                );
            }
            catch (InvalidOperationException ex)
                when (ex.Message is "HISTORY_OPERATION_IN_PROGRESS" or "SESSION_BUSY")
            {
                handedOff = true;
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    ex.Message,
                    HistoryErrorMessage(ex.Message, "会话忙")
                );
            }
            catch (Exception ex)
            {
                handedOff = true;
                HaloLogger.App.Error(
                    $"[History] replay.failed ws={workspaceId} sess={sessionId} anchor={anchorMessageId}",
                    ex
                );
                return BridgeResponse<HistoryTruncateResult>.Fail(
                    "TRUNCATE_ERROR",
                    HistoryErrorMessage("TRUNCATE_ERROR", "截断失败")
                );
            }
            finally
            {
                if (!handedOff)
                    lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"TruncateSessionHistory failed for session '{sessionId}'", ex);
            return BridgeResponse<HistoryTruncateResult>.Fail(
                "TRUNCATE_ERROR",
                HistoryErrorMessage("TRUNCATE_ERROR", "截断失败")
            );
        }
    }

    public async Task<BridgeResponse<SessionMetadata>> BranchSession(
        string workspaceId,
        string sourceSessionId,
        string anchorMessageId,
        bool includeAnchor
    )
    {
        try
        {
            if (
                string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sourceSessionId)
            )
                return BridgeResponse<SessionMetadata>.Fail("INVALID_SESSION", "工作区或会话不能为空");
            if (await IsSubSessionAsync(workspaceId, sourceSessionId).ConfigureAwait(false))
                return BridgeResponse<SessionMetadata>.Fail(
                    "SUB_READONLY",
                    HistoryErrorMessage("SUB_READONLY", "子会话不可操作")
                );
            var lease = AcquireHistoryLock(workspaceId, sourceSessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<SessionMetadata>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );

            bool handedOff = false;
            try
            {
                var created = await _workspaceSessionService
                    .BranchSessionAsync(
                        workspaceId,
                        sourceSessionId,
                        anchorMessageId,
                        includeAnchor,
                        default,
                        lease
                    )
                    .ConfigureAwait(false);
                handedOff = true;
                return BridgeResponse<SessionMetadata>.Success(created);
            }
            catch (BranchRollbackException ex)
            {
                handedOff = true;
                HaloLogger.App.Error(
                    "[History] branch.rollback.failed new="
                        + ex.NewSessionId
                        + " stages="
                        + ex.FailedStages,
                    ex
                );
                return BridgeResponse<SessionMetadata>.Fail(
                    "BRANCH_ROLLBACK_FAILED",
                    HistoryErrorMessage("BRANCH_ROLLBACK_FAILED", "分支清理失败")
                        + "（残留会话 "
                        + ex.NewSessionId
                        + "，阶段 "
                        + ex.FailedStages
                        + "）",
                    ex.NewSessionId,
                    ex.FailedStages
                );
            }
            catch (InvalidOperationException ex)
                when (ex.Message
                        is "ANCHOR_NOT_FOUND"
                            or "SESSION_NOT_FOUND"
                            or "INVALID_BRANCH_REQUEST"
                            or "BRANCH_COPY_ERROR"
                            or "HISTORY_OPERATION_IN_PROGRESS"
                            or "SESSION_BUSY"
                )
            {
                handedOff = true;
                return BridgeResponse<SessionMetadata>.Fail(
                    ex.Message,
                    HistoryErrorMessage(ex.Message, "分支失败")
                );
            }
            catch (Exception ex)
            {
                handedOff = true;
                HaloLogger.App.Error(
                    $"[History] branch.failed ws={workspaceId} src={sourceSessionId}",
                    ex
                );
                return BridgeResponse<SessionMetadata>.Fail(
                    "BRANCH_ERROR",
                    HistoryErrorMessage("BRANCH_ERROR", "分支失败")
                );
            }
            finally
            {
                if (!handedOff)
                    lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"BranchSession failed for session '{sourceSessionId}'", ex);
            return BridgeResponse<SessionMetadata>.Fail(
                "BRANCH_ERROR",
                HistoryErrorMessage("BRANCH_ERROR", "分支失败")
            );
        }
    }

    public async Task<BridgeResponse<HistoryHealResult>> HealSessionHistory(
        string workspaceId,
        string sessionId
    )
    {
        try
        {
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<HistoryHealResult>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );
            bool handedOff = false;
            try
            {
                var result = await _workspaceSessionService
                    .HealSessionHistoryAsync(workspaceId, sessionId, default, lease)
                    .ConfigureAwait(false);
                handedOff = true;
                if (!result.Success)
                    return BridgeResponse<HistoryHealResult>.Fail(
                        result.ErrorCode ?? "HEAL_FAILED",
                        HistoryErrorMessage(result.ErrorCode, "愈合失败")
                    );
                return BridgeResponse<HistoryHealResult>.Success(result);
            }
            finally
            {
                if (!handedOff)
                    lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"HealSessionHistory failed for session '{sessionId}'", ex);
            return BridgeResponse<HistoryHealResult>.Fail(
                "HEAL_ERROR",
                HistoryErrorMessage("HEAL_ERROR", "愈合失败")
            );
        }
    }

    [Obsolete("迁移期兼容入口：新 UI 使用 DeleteSessionTurn，仅保留给旧前端。")]
    public async Task<BridgeResponse<HistoryDeleteResult>> DeleteSessionMessage(
        string workspaceId,
        string sessionId,
        string messageId
    )
    {
        try
        {
            if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<HistoryDeleteResult>.Fail(
                    "SUB_READONLY",
                    HistoryErrorMessage("SUB_READONLY", "子会话不可操作")
                );
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<HistoryDeleteResult>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );
            bool handedOff = false;
            try
            {
                var result = await _workspaceSessionService
                    .DeleteSessionMessageAsync(workspaceId, sessionId, messageId, default, lease)
                    .ConfigureAwait(false);
                handedOff = true;
                if (!result.Success)
                    return BridgeResponse<HistoryDeleteResult>.Fail(
                        result.ErrorCode ?? "DELETE_FAILED",
                        HistoryErrorMessage(result.ErrorCode, "删除失败")
                    );
                return BridgeResponse<HistoryDeleteResult>.Success(result);
            }
            finally
            {
                if (!handedOff)
                    lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"DeleteSessionMessage failed for session '{sessionId}' msg '{messageId}'",
                ex
            );
            return BridgeResponse<HistoryDeleteResult>.Fail(
                "DELETE_ERROR",
                HistoryErrorMessage("DELETE_ERROR", "删除失败")
            );
        }
    }

    /// <summary>删除锚点所属整轮：新 UI 唯一删除入口，失败按稳定错误码返回。</summary>
    public async Task<BridgeResponse<HistoryTurnDeleteResult>> DeleteSessionTurn(
        string workspaceId,
        string sessionId,
        string anchorMessageId
    )
    {
        try
        {
            if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<HistoryTurnDeleteResult>.Fail(
                    "SUB_READONLY",
                    HistoryErrorMessage("SUB_READONLY", "子会话不可操作")
                );
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<HistoryTurnDeleteResult>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );
            bool handedOff = false;
            try
            {
                var result = await _workspaceSessionService
                    .DeleteSessionTurnAsync(workspaceId, sessionId, anchorMessageId, default, lease)
                    .ConfigureAwait(false);
                handedOff = true;
                if (!result.Success)
                    return BridgeResponse<HistoryTurnDeleteResult>.Fail(
                        result.ErrorCode ?? "DELETE_FAILED",
                        HistoryErrorMessage(result.ErrorCode, "整轮删除失败")
                    );
                return BridgeResponse<HistoryTurnDeleteResult>.Success(result);
            }
            finally
            {
                if (!handedOff)
                    lease.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"DeleteSessionTurn failed for session '{sessionId}' anchor '{anchorMessageId}'",
                ex
            );
            return BridgeResponse<HistoryTurnDeleteResult>.Fail(
                "DELETE_ERROR",
                HistoryErrorMessage("DELETE_ERROR", "整轮删除失败")
            );
        }
    }

    /// <summary>
    /// 启动时全量扫描：遍历全部工作区/会话做孤儿愈合（只读无伤会话直接跳过，只写脏会话）。
    /// 后台 fire-and-forget 调用，永不阻塞 UI。支持取消；逐项异常进日志不断链。
    /// </summary>
    public Task<BridgeResponse<int>> ScanAndHealAllSessions() =>
        ScanAndHealAllSessions(CancellationToken.None);

    public async Task<BridgeResponse<int>> ScanAndHealAllSessions(
        CancellationToken cancellationToken
    )
    {
        int healedSessions = 0;
        int sessionDirTotal = CountSessionDirectories();
        if (ShouldDeferStartupScan(sessionDirTotal))
        {
            HaloLogger.App.Info(
                "[History] startup scan deferred: "
                    + sessionDirTotal
                    + " session dirs exceed threshold "
                    + StartupScanLazyThreshold
                    + ", integrity is checked lazily on open."
            );
            return BridgeResponse<int>.Success(0, "scan deferred, lazy check on open");
        }
        try
        {
            var workspaces = await _workspaceSessionService
                .GetAllWorkspacesAsync()
                .ConfigureAwait(false);
            foreach (var ws in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<SessionMetadata> sessions;
                try
                {
                    sessions = await _workspaceSessionService
                        .GetSessionsByWorkspaceAsync(ws.Id)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn(
                        $"ScanAndHealAllSessions: list sessions failed for workspace '{ws.Id}', skipped.",
                        ex
                    );
                    continue;
                }
                foreach (var sess in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var r = await _workspaceSessionService
                            .HealSessionHistoryAsync(ws.Id, sess.Id, cancellationToken)
                            .ConfigureAwait(false);
                        if (r.Success && (r.SyntheticAppended > 0 || r.OrphanToolsRemoved > 0))
                            healedSessions++;
                        try
                        {
                            var gcStore = new Infrastructure.Storage.SessionHistoryStore(
                                new Infrastructure.Storage.SessionHistoryDb(_storageManager)
                            );
                            await gcStore
                                .CollectOrphanToolOutputsAsync(ws.Id, sess.Id, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        HaloLogger.App.Warn(
                            $"ScanAndHealAllSessions: heal failed for session '{sess.Id}' in workspace '{ws.Id}', skipped.",
                            ex
                        );
                    }
                }
            }
            HaloLogger.App.Info(
                $"[History] startup orphan scan done, healedSessions={healedSessions}"
            );
            return BridgeResponse<int>.Success(healedSessions);
        }
        catch (OperationCanceledException)
        {
            HaloLogger.App.Info("[History] startup orphan scan cancelled.");
            return BridgeResponse<int>.Fail("SCAN_CANCELLED", "扫描已取消");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ScanAndHealAllSessions failed", ex);
            return BridgeResponse<int>.Fail("SCAN_ERROR", "扫描失败");
        }
    }

    internal const int StartupScanLazyThreshold = 200;

    internal static bool ShouldDeferStartupScan(int totalSessions) =>
        totalSessions > StartupScanLazyThreshold;

    private int CountSessionDirectories()
    {
        try
        {
            string root = _storageManager.SessionsDirectory;
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root))
            {
                return 0;
            }
            int total = 0;
            foreach (string wsDir in System.IO.Directory.EnumerateDirectories(root))
            {
                try
                {
                    foreach (string _ in System.IO.Directory.EnumerateDirectories(wsDir))
                    {
                        total++;
                        if (total > StartupScanLazyThreshold)
                        {
                            return total;
                        }
                    }
                }
                catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public Task<BridgeResponse<CompactionRepairReport>> AnalyzeCompactionHistory(
        string workspaceId,
        string sessionId
    ) => RepairCompactionHistory(workspaceId, sessionId, false);

    public async Task<BridgeResponse<CompactionRepairReport>> RepairCompactionHistory(
        string workspaceId,
        string sessionId,
        bool apply
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
                return BridgeResponse<CompactionRepairReport>.Fail("INVALID_SESSION", "工作区或会话不能为空");
            if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<CompactionRepairReport>.Fail("SUB_READONLY", "子会话不可操作");
            if (_workspaceSessionService is not Services.WorkspaceSessionService concrete)
                return BridgeResponse<CompactionRepairReport>.Fail(
                    "UNSUPPORTED_SERVICE",
                    "当前会话服务不支持压缩历史修复"
                );
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<CompactionRepairReport>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );
            using (lease)
            {
                var repair = new Services.CompactionHistoryRepair(concrete);
                var report = await repair
                    .RepairAsync(workspaceId, sessionId, apply)
                    .ConfigureAwait(false);
                return BridgeResponse<CompactionRepairReport>.Success(report);
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"RepairCompactionHistory failed for session '{sessionId}'", ex);
            return BridgeResponse<CompactionRepairReport>.Fail("COMPACT_REPAIR_ERROR", "压缩历史修复失败");
        }
    }

    /// <summary>压缩备份恢复入口：删桩与全量重写统一走历史写锁。</summary>
    public async Task<BridgeResponse<HistoryRestoreResult>> RestoreCompactedHistory(
        string workspaceId,
        string sessionId,
        string backupFileName
    )
    {
        try
        {
            var lease = AcquireHistoryLock(workspaceId, sessionId, out var busyCode);
            if (lease == null)
                return BridgeResponse<HistoryRestoreResult>.Fail(
                    busyCode!,
                    HistoryErrorMessage(busyCode, "会话忙")
                );
            using (lease)
            {
                if (_workspaceSessionService is not Services.WorkspaceSessionService concrete)
                    return BridgeResponse<HistoryRestoreResult>.Fail(
                        "UNSUPPORTED_SERVICE",
                        "当前会话服务不支持恢复"
                    );
                var tool = new Services.CompactionRestore(concrete);
                var result = await tool.RestoreAsync(workspaceId, sessionId, backupFileName)
                    .ConfigureAwait(false);
                if (!result.Success)
                    return BridgeResponse<HistoryRestoreResult>.Fail(
                        result.ErrorCode ?? "RESTORE_FAILED",
                        HistoryErrorMessage(result.ErrorCode, "恢复失败")
                    );
                return BridgeResponse<HistoryRestoreResult>.Success(result);
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"RestoreCompactedHistory failed for session '{sessionId}'", ex);
            return BridgeResponse<HistoryRestoreResult>.Fail("RESTORE_ERROR", "恢复失败");
        }
    }
}
