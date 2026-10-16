using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Bridge;

public sealed class AgentConfigWithMeta : AgentConfigDocument
{
    public bool IsDefault { get; set; }
    public string? FallbackReason { get; set; }
}

/// <summary>need08 §6：智能体行为配置 Bridge（对标 HostBridgeFacade.Prompt.cs）。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<AgentConfigWithMeta>> GetAgentConfig()
    {
        try
        {
            var (doc, isDefault, fallback) = AgentConfigService.LoadWithMeta();
            return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Success(ToAgentWithMeta(doc, isDefault, fallback)));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetAgentConfig failed", ex);
            return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_GET_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<AgentConfigWithMeta>> SaveAgentConfig(string configJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", "配置 JSON 不能为空"));
            AgentConfigDocument? incoming;
            try
            {
                incoming = AgentJsonOptions.DeserializeConfigDocument(configJson);
            }
            catch (Exception ex)
            {
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", "配置 JSON 解析失败：" + ex.Message));
            }
            if (incoming == null)
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", "配置 JSON 解析为空"));
            try
            {
                var saved = _agentConfigService.SaveAgentConfig(incoming);
                JsEventDispatcher.Publish("config:agent-changed", saved);
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Success(ToAgentWithMeta(saved, false, null), "智能体行为配置已保存，下一轮对话起生效"));
            }
            catch (AgentConfigValidationException vex)
            {
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", $"字段 [{vex.Field}]：{vex.Message}"));
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveAgentConfig failed", ex);
            return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_SAVE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<AgentConfigWithMeta>> ResetAgentConfig(string? field)
    {
        try
        {
            try
            {
                var reset = _agentConfigService.ResetAgentConfig(field);
                JsEventDispatcher.Publish("config:agent-changed", reset);
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Success(ToAgentWithMeta(reset, false, null), "已恢复默认"));
            }
            catch (AgentConfigValidationException vex)
            {
                return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", $"字段 [{vex.Field}]：{vex.Message}"));
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ResetAgentConfig failed", ex);
            return Task.FromResult(BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_RESET_ERROR", ex.Message));
        }
    }

    private static AgentConfigWithMeta ToAgentWithMeta(AgentConfigDocument doc, bool isDefault, string? fallback)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(doc, HaloJsonOptions.IndentedCamelCase);
        var meta = System.Text.Json.JsonSerializer.Deserialize<AgentConfigWithMeta>(json, HaloJsonOptions.IndentedCamelCase)
            ?? new AgentConfigWithMeta();
        meta.IsDefault = isDefault;
        meta.FallbackReason = fallback;
        return meta;
    }
}
