using Host.Desktop.Core.Models;

namespace Host.Desktop.Bridge;

/// <summary>
/// need21-B02 Step1: 桥门面接口（只抽签名，不动实现）。
/// 首批收录无参/单参代表方法；多参方法签名在 B03~B05 补全前仍由具体类直接承载。
/// MainForm 与新测试依赖此接口。
/// </summary>
public interface IHostBridgeFacade
{
    void AttachForm(Form form);
    Task<T> InvokeOnUiThreadAsync<T>(Func<T> action);
    Task<BridgeResponse<SystemRuntimeInfo>> GetSystemInfo();
    Task<BridgeResponse<StorageConfigInfo>> GetStorageConfig();
    Task<BridgeResponse<ProvidersConfigDocument>> GetProvidersConfig();
    Task<BridgeResponse<string>> StartSessionRun(string requestJson);
    Task<BridgeResponse<bool>> CancelSessionRun(string sessionId);
    Task<BridgeResponse<bool>> CancelAgentTurn(string sessionId);
    Task<BridgeResponse<bool>> CancelAgentTree(string mainSessionId);
    Task<BridgeResponse<bool>> CancelCompaction(string sessionId);
    Task<BridgeResponse<List<WorkspaceInfo>>> GetWorkspaces();
    Task<BridgeResponse<List<SessionMetadata>>> GetSessions(string workspaceId);
    Task<BridgeResponse<string>> GetToolDefinitions(string protocol = "chat");
    Task<BridgeResponse<AgentConfigWithMeta>> GetAgentConfig();
    Task<BridgeResponse<PromptConfigWithMeta>> GetPromptConfig();
    Task<BridgeResponse<SessionGoal>> GetGoal(string workspaceId, string sessionId);
    Task<BridgeResponse<object>> GetUsageFilterOptions();
    Task<BridgeResponse<UiActiveStateDto>> GetLastActive();
}
