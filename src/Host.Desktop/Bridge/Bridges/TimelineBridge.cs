using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>Timeline summary bridge: directory and process box counts come straight from the timeline store.</summary>
public sealed partial class HostBridgeFacade
{
    private WorkspaceSessionStore CreateWorkspaceStore()
    {
        return new WorkspaceSessionStore(_storageManager, _sessionRepository);
    }

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

            var probe = new SessionHistoryStore(_storageManager);
            try
            {
                await probe.CheckOnceAsync(workspaceId, sessionId).ConfigureAwait(false);
            }
            catch { }
            var store = CreateWorkspaceStore();
            var summaries = await store
                .GetSummaryAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            var messageRefs = await store
                .GetMessageRefsAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            HistoryAnchorProjector.Project(summaries, messageRefs);
            return BridgeResponse<List<TurnSummary>>.Success(summaries);
        }
        catch (LegacySessionFormatException ex)
        {
            return BridgeResponse<List<TurnSummary>>.Fail("LEGACY_SESSION", ex.Message);
        }
        catch (UnsupportedFormatException ex)
        {
            return BridgeResponse<List<TurnSummary>>.Fail("UNSUPPORTED_FORMAT", ex.Message);
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
            var store = CreateWorkspaceStore();
            try
            {
                await store.GetSummaryAsync(workspaceId, sessionId).ConfigureAwait(false);
            }
            catch (LegacySessionFormatException ex)
            {
                return BridgeResponse<TimelineBlockContentsResponse>.Fail(
                    "LEGACY_SESSION",
                    ex.Message
                );
            }
            catch (UnsupportedFormatException ex)
            {
                return BridgeResponse<TimelineBlockContentsResponse>.Fail(
                    "UNSUPPORTED_FORMAT",
                    ex.Message
                );
            }
            var probe = new SessionHistoryStore(_storageManager);
            var (items, missing) = await probe
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
            var store = CreateWorkspaceStore();
            TimelineToolResult? result;
            try
            {
                result = await store
                    .GetToolResultAsync(
                        workspaceId,
                        sessionId,
                        turnId ?? string.Empty,
                        callId ?? string.Empty
                    )
                    .ConfigureAwait(false);
            }
            catch (LegacySessionFormatException ex)
            {
                return BridgeResponse<TimelineToolResult>.Fail("LEGACY_SESSION", ex.Message);
            }
            catch (UnsupportedFormatException ex)
            {
                return BridgeResponse<TimelineToolResult>.Fail("UNSUPPORTED_FORMAT", ex.Message);
            }
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
            var store = CreateWorkspaceStore();
            List<TimelineBlockContent> blocks;
            try
            {
                blocks = await store
                    .GetBlockIdsAsync(workspaceId, sessionId, turnId ?? string.Empty)
                    .ConfigureAwait(false);
            }
            catch (LegacySessionFormatException ex)
            {
                return BridgeResponse<List<string>>.Fail("LEGACY_SESSION", ex.Message);
            }
            catch (UnsupportedFormatException ex)
            {
                return BridgeResponse<List<string>>.Fail("UNSUPPORTED_FORMAT", ex.Message);
            }
            var ids = new List<string>(blocks.Count);
            foreach (var block in blocks)
            {
                if (block != null && !string.IsNullOrWhiteSpace(block.BlockId))
                {
                    ids.Add(block.BlockId);
                }
            }
            return BridgeResponse<List<string>>.Success(ids);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetTurnBlockIds failed for session '" + sessionId + "'", ex);
            return BridgeResponse<List<string>>.Fail("TIMELINE_ERROR", ex.Message);
        }
    }
}
