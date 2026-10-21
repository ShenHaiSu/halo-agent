using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Bridge;

/// <summary>need21-B02: 网络代理。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<GlobalNetworkSettings>> GetNetworkSettings()
    {
        try
        {
            var settings = _providerConfigService.GetNetworkSettingsForClient();
            return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Success(settings));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetNetworkSettings failed", ex);
            return Task.FromResult(
                BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_GET_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<GlobalNetworkSettings>> SaveNetworkSettings(string settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return Task.FromResult(
                    BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_SAVE_INVALID", "网络配置参数不能为空")
                );
            }

            var incoming = JsonSerializer.Deserialize<GlobalNetworkSettings>(
                settingsJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (incoming == null)
            {
                return Task.FromResult(
                    BridgeResponse<GlobalNetworkSettings>.Fail(
                        "NETWORK_SAVE_INVALID",
                        "网络配置 JSON 解析失败"
                    )
                );
            }

            var saved = _providerConfigService.SaveNetworkSettings(incoming);
            JsEventDispatcher.Publish("config:network-changed", saved);
            return Task.FromResult(
                BridgeResponse<GlobalNetworkSettings>.Success(saved, "网络与代理设置已保存并立即生效")
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveNetworkSettings failed", ex);
            return Task.FromResult(
                BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_SAVE_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<SystemProxySnapshot>> InspectSystemProxy(
        string? probeTargetUrl = null
    )
    {
        try
        {
            string target = string.IsNullOrWhiteSpace(probeTargetUrl)
                ? "https://api.openai.com"
                : probeTargetUrl.Trim();
            var snapshot = SystemProxyInspector.InspectCurrentSystemProxy(target);
            return Task.FromResult(BridgeResponse<SystemProxySnapshot>.Success(snapshot));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("InspectSystemProxy failed", ex);
            return Task.FromResult(
                BridgeResponse<SystemProxySnapshot>.Fail("PROXY_INSPECT_ERROR", ex.Message)
            );
        }
    }

    public async Task<BridgeResponse<ProxyConnectivityTestResult>> TestProxyConnectivity(
        string? targetUrl = null,
        string? overrideSettingsJson = null,
        string? providerJson = null
    )
    {
        try
        {
            GlobalNetworkSettings? overrideSettings = null;
            if (!string.IsNullOrWhiteSpace(overrideSettingsJson))
            {
                overrideSettings = JsonSerializer.Deserialize<GlobalNetworkSettings>(
                    overrideSettingsJson,
                    HaloJsonOptions.IndentedCamelCase
                );
            }

            ProviderConfig? provider = null;
            if (!string.IsNullOrWhiteSpace(providerJson))
            {
                provider = JsonSerializer.Deserialize<ProviderConfig>(
                    providerJson,
                    HaloJsonOptions.IndentedCamelCase
                );
                if (
                    provider != null
                    && string.IsNullOrWhiteSpace(provider.ApiKeyEncrypted)
                    && !string.IsNullOrWhiteSpace(provider.Id)
                )
                {
                    var persisted = _providerConfigService.GetProviderById(provider.Id);
                    if (persisted != null)
                    {
                        provider.ApiKeyEncrypted = persisted.ApiKeyEncrypted;
                    }
                }
            }

            var result = await _httpClientFactory
                .TestProxyConnectivityAsync(
                    targetUrl: targetUrl,
                    overrideGlobalSettings: overrideSettings,
                    provider: provider
                )
                .ConfigureAwait(false);

            return BridgeResponse<ProxyConnectivityTestResult>.Success(
                result,
                result.Success
                    ? $"连通测试成功 ({result.ElapsedMs} ms · {result.EffectiveProxyUsed})"
                    : result.ErrorMessage ?? "连通测试失败"
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("TestProxyConnectivity failed", ex);
            return BridgeResponse<ProxyConnectivityTestResult>.Fail("PROXY_TEST_ERROR", ex.Message);
        }
    }
}
