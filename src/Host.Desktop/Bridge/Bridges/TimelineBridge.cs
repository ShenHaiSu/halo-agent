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
}
