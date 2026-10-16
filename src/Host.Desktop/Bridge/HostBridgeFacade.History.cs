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
}
