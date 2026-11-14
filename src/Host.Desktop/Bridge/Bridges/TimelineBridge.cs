using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Bridge;

/// <summary>Timeline summary bridge: directory and process box counts come straight from the timeline store.</summary>
public sealed partial class HostBridgeFacade
{
    public async Task<BridgeResponse<List<TurnSummary>>> GetTimelineSummary(
        string workspaceId,
        string sessionId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<List<TurnSummary>>.Fail(
                    "INVALID_SESSION",
                    "Workspace and session must not be empty."
                );
            }

            var store = new SessionHistoryStore(_storageManager);
            try
            {
                await store.CheckOnceAsync(workspaceId, sessionId).ConfigureAwait(false);
            }
            catch { }
            var summaries = await store
                .GetTimelineSummaryAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            return BridgeResponse<List<TurnSummary>>.Success(summaries);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetTimelineSummary failed for session '" + sessionId + "'", ex);
            return BridgeResponse<List<TurnSummary>>.Fail("TIMELINE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<TimelineBlockContentsResponse>> GetBlockContents(
        string workspaceId,
        string sessionId,
        string[] blockIds
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<TimelineBlockContentsResponse>.Fail(
                    "INVALID_SESSION",
                    "Workspace and session must not be empty."
                );
            }
            var store = new SessionHistoryStore(_storageManager);
            var (items, missing) = await store
                .GetBlockContentsAsync(workspaceId, sessionId, blockIds ?? Array.Empty<string>())
                .ConfigureAwait(false);
            return BridgeResponse<TimelineBlockContentsResponse>.Success(
                new TimelineBlockContentsResponse { Items = items, Missing = missing }
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetBlockContents failed for session '" + sessionId + "'", ex);
            return BridgeResponse<TimelineBlockContentsResponse>.Fail("TIMELINE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<TimelineToolResult>> GetToolResult(
        string workspaceId,
        string sessionId,
        string turnId,
        string callId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<TimelineToolResult>.Fail(
                    "INVALID_SESSION",
                    "Workspace and session must not be empty."
                );
            }
            var store = new SessionHistoryStore(_storageManager);
            var result = await store
                .GetToolResultAsync(
                    workspaceId,
                    sessionId,
                    turnId ?? string.Empty,
                    callId ?? string.Empty
                )
                .ConfigureAwait(false);
            if (result == null)
            {
                return BridgeResponse<TimelineToolResult>.Fail(
                    "BLOCK_MISSING",
                    "Tool result not found."
                );
            }
            return BridgeResponse<TimelineToolResult>.Success(result);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetToolResult failed for session '" + sessionId + "'", ex);
            return BridgeResponse<TimelineToolResult>.Fail("TIMELINE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<List<string>>> GetTurnBlockIds(
        string workspaceId,
        string sessionId,
        string turnId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<List<string>>.Fail(
                    "INVALID_SESSION",
                    "Workspace and session must not be empty."
                );
            }
            var store = new SessionHistoryStore(_storageManager);
            var ids = await store
                .GetBlockIdsByTurnAsync(workspaceId, sessionId, turnId ?? string.Empty)
                .ConfigureAwait(false);
            return BridgeResponse<List<string>>.Success(ids);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetTurnBlockIds failed for session '" + sessionId + "'", ex);
            return BridgeResponse<List<string>>.Fail("TIMELINE_ERROR", ex.Message);
        }
    }
}
