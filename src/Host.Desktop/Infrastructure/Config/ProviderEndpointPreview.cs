using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

/// <summary>
/// Provider 端点与自定义 Header 预览结果
/// </summary>
public sealed class ProviderEndpointPreviewDto
{
    public string BaseUrl { get; set; } = string.Empty;
    public string NormalizedVersionPrefix { get; set; } = string.Empty;
    public string ChatEndpointUrl { get; set; } = string.Empty;
    public string RespEndpointUrl { get; set; } = string.Empty;
    public string ModelsEndpointUrl { get; set; } = string.Empty;
    public string EffectiveProxySummary { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionHash { get; set; } = string.Empty;
    public string WorkspaceName { get; set; } = string.Empty;
    public string WorkspaceHash { get; set; } = string.Empty;
    public Dictionary<string, string> ResolvedHeaders { get; set; } = new();
}
