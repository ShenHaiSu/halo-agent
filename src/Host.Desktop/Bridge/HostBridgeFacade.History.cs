using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Bridge;

/// <summary>need07：历史右键（截断/分支）Bridge 入口。need16：子会话只读守卫。</summary>
public sealed partial class HostBridgeFacade
{
    private bool IsHistoryBusy(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        string sid = sessionId.Trim();
        return _activeSessionTokens.ContainsKey("agent:" + sid)
            || _activeSessionTokens.ContainsKey("compact:" + sid);
    }

    private async Task<bool> IsSubSessionAsync(string workspaceId, string sessionId)
    {
        try
        {
            var meta = await _workspaceSessionService.GetSessionAsync(workspaceId, sessionId).ConfigureAwait(false);
            return IsSubReadOnly(meta);
        }
        catch { return false; }
    }

    public async Task<BridgeResponse<HistoryTruncateResult>> TruncateSessionHistory(
        string workspaceId, string sessionId, string anchorMessageId, bool removeAnchor)
    {
        try
        {
            if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                return BridgeResponse<HistoryTruncateResult>.Fail("SUB_READONLY", "子会话历史只读，不可截断");
            if (IsHistoryBusy(sessionId))
                return BridgeResponse<HistoryTruncateResult>.Fail("SESSION_BUSY", "生成或压缩进行中，请稍后再试");
            var result = await _workspaceSessionService.TruncateSessionHistoryAsync(
                workspaceId, sessionId, anchorMessageId, removeAnchor).ConfigureAwait(false);
            if (!result.Success)
                return BridgeResponse<HistoryTruncateResult>.Fail(result.ErrorCode ?? "TRUNCATE_FAILED", result.ErrorCode ?? "截断失败");
            return BridgeResponse<HistoryTruncateResult>.Success(result);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"TruncateSessionHistory failed for session '{sessionId}'", ex);
            return BridgeResponse<HistoryTruncateResult>.Fail("TRUNCATE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<SessionMetadata>> BranchSession(
        string workspaceId, string sourceSessionId, string anchorMessageId, bool includeAnchor)
    {
        try
        {
            if (await IsSubSessionAsync(workspaceId, sourceSessionId).ConfigureAwait(false))
                return BridgeResponse<SessionMetadata>.Fail("SUB_READONLY", "子会话历史只读，不可分支");
            if (IsHistoryBusy(sourceSessionId))
                return BridgeResponse<SessionMetadata>.Fail("SESSION_BUSY", "生成或压缩进行中，请稍后再试");
            var created = await _workspaceSessionService.BranchSessionAsync(
                workspaceId, sourceSessionId, anchorMessageId, includeAnchor).ConfigureAwait(false);
            return BridgeResponse<SessionMetadata>.Success(created);
        }
        catch (InvalidOperationException ex) when (ex.Message is "ANCHOR_NOT_FOUND" or "SESSION_NOT_FOUND")
        {
            return BridgeResponse<SessionMetadata>.Fail(ex.Message, ex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"BranchSession failed for session '{sourceSessionId}'", ex);
            return BridgeResponse<SessionMetadata>.Fail("BRANCH_ERROR", ex.Message);
        }
    }
     public async Task<BridgeResponse<HistoryHealResult>> HealSessionHistory(string workspaceId, string sessionId)
     {
         try
         {
             if (IsHistoryBusy(sessionId))
                 return BridgeResponse<HistoryHealResult>.Fail("SESSION_BUSY", "生成或压缩进行中，请稍后再试");
             var result = await _workspaceSessionService.HealSessionHistoryAsync(
                 workspaceId, sessionId).ConfigureAwait(false);
             if (!result.Success)
                 return BridgeResponse<HistoryHealResult>.Fail(result.ErrorCode ?? "HEAL_FAILED", result.ErrorCode ?? "愈合失败");
             return BridgeResponse<HistoryHealResult>.Success(result);
         }
         catch (Exception ex)
         {
             HaloLogger.App.Error($"HealSessionHistory failed for session '{sessionId}'", ex);
             return BridgeResponse<HistoryHealResult>.Fail("HEAL_ERROR", ex.Message);
         }
     }

     public async Task<BridgeResponse<HistoryDeleteResult>> DeleteSessionMessage(
         string workspaceId, string sessionId, string messageId)
     {
         try
         {
             if (await IsSubSessionAsync(workspaceId, sessionId).ConfigureAwait(false))
                 return BridgeResponse<HistoryDeleteResult>.Fail("SUB_READONLY", "子会话历史只读，不可删除");
             if (IsHistoryBusy(sessionId))
                 return BridgeResponse<HistoryDeleteResult>.Fail("SESSION_BUSY", "生成或压缩进行中，请稍后再试");
             var result = await _workspaceSessionService.DeleteSessionMessageAsync(
                 workspaceId, sessionId, messageId).ConfigureAwait(false);
             if (!result.Success)
                 return BridgeResponse<HistoryDeleteResult>.Fail(result.ErrorCode ?? "DELETE_FAILED", result.ErrorCode ?? "删除失败");
             return BridgeResponse<HistoryDeleteResult>.Success(result);
         }
         catch (Exception ex)
         {
             HaloLogger.App.Error($"DeleteSessionMessage failed for session '{sessionId}' msg '{messageId}'", ex);
             return BridgeResponse<HistoryDeleteResult>.Fail("DELETE_ERROR", ex.Message);
         }
     }

    /// <summary>
    /// 启动时全量扫描：遍历全部工作区/会话做孤儿愈合（只读无伤会话直接跳过，只写脏会话）。
    /// 后台 fire-and-forget 调用，永不阻塞 UI。支持取消；逐项异常进日志不断链（need22-P4 H9）。
    /// </summary>
    public Task<BridgeResponse<int>> ScanAndHealAllSessions() => ScanAndHealAllSessions(CancellationToken.None);

    public async Task<BridgeResponse<int>> ScanAndHealAllSessions(CancellationToken cancellationToken)
    {
        int healedSessions = 0;
        try
        {
            var workspaces = await _workspaceSessionService.GetAllWorkspacesAsync().ConfigureAwait(false);
            foreach (var ws in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<SessionMetadata> sessions;
                try { sessions = await _workspaceSessionService.GetSessionsByWorkspaceAsync(ws.Id).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn($"ScanAndHealAllSessions: list sessions failed for workspace '{ws.Id}', skipped.", ex);
                    continue;
                }
                foreach (var sess in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var r = await _workspaceSessionService.HealSessionHistoryAsync(ws.Id, sess.Id).ConfigureAwait(false);
                        if (r.Success && (r.SyntheticAppended > 0 || r.OrphanToolsRemoved > 0)) healedSessions++;
                    }
                    catch (Exception ex)
                    {
                        HaloLogger.App.Warn($"ScanAndHealAllSessions: heal failed for session '{sess.Id}' in workspace '{ws.Id}', skipped.", ex);
                    }
                }
            }
            HaloLogger.App.Info($"[History] startup orphan scan done, healedSessions={healedSessions}");
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
            return BridgeResponse<int>.Fail("SCAN_ERROR", ex.Message);
        }
    }
}
