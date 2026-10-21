using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Bridge;

/// <summary>UI 活跃状态 Bridge：上次离开时的工作区+会话（独立 config/ui-state.json，只恢复到 main；单实例互斥）。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<UiActiveStateDto>> GetLastActive()
    {
        try
        {
            var s = _storageManager.LoadUiState();
            return Task.FromResult(BridgeResponse<UiActiveStateDto>.Success(new UiActiveStateDto
            {
                WorkspaceId = s.LastWorkspaceId,
                SessionId = s.LastSessionId,
            }));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetLastActive failed", ex);
            return Task.FromResult(BridgeResponse<UiActiveStateDto>.Fail(ex.Message, "UISTATE_ERROR"));
        }
    }

    public async Task<BridgeResponse<bool>> SetLastActive(string? workspaceId, string? sessionId)
    {
        try
        {
            var s = _storageManager.LoadUiState();
            s.LastWorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
            s.LastSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
            await _storageManager.SaveUiStateAsync(s).ConfigureAwait(false);
            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetLastActive failed", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "UISTATE_ERROR");
        }
    }
}
