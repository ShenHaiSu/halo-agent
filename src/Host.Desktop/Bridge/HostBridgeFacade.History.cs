using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Bridge;

/// <summary>need07：历史右键（截断/分支）Bridge 入口。</summary>
public sealed partial class HostBridgeFacade
{
    private bool IsHistoryBusy(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        string sid = sessionId.Trim();
        return _activeSessionTokens.ContainsKey("agent:" + sid)
            || _activeSessionTokens.ContainsKey("compact:" + sid);
    }

    public async Task<BridgeResponse<HistoryTruncateResult>> TruncateSessionHistory(
        string workspaceId, string sessionId, string anchorMessageId, bool removeAnchor)
    {
        try
        {
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
     /// 后台 fire-and-forget 调用，永不阻塞 UI。
     /// </summary>
     public async Task<BridgeResponse<int>> ScanAndHealAllSessions()
     {
         int healedSessions = 0;
         try
         {
             var workspaces = await _workspaceSessionService.GetAllWorkspacesAsync().ConfigureAwait(false);
             foreach (var ws in workspaces)
             {
                 List<SessionMetadata> sessions;
                 try { sessions = await _workspaceSessionService.GetSessionsByWorkspaceAsync(ws.Id).ConfigureAwait(false); }
                 catch { continue; }
                 foreach (var sess in sessions)
                 {
                     try
                     {
                         var r = await _workspaceSessionService.HealSessionHistoryAsync(ws.Id, sess.Id).ConfigureAwait(false);
                         if (r.Success && (r.SyntheticAppended > 0 || r.OrphanToolsRemoved > 0)) healedSessions++;
                     }
                     catch { /* 单会话失败不影响其余 */ }
                 }
             }
             HaloLogger.App.Info($"[History] startup orphan scan done, healedSessions={healedSessions}");
             return BridgeResponse<int>.Success(healedSessions);
         }
         catch (Exception ex)
         {
             HaloLogger.App.Error("ScanAndHealAllSessions failed", ex);
             return BridgeResponse<int>.Fail("SCAN_ERROR", ex.Message);
         }
     }
}
