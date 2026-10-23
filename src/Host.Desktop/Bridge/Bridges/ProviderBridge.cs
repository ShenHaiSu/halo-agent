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

/// <summary>need21-B02: Provider 配置。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<ProvidersConfigDocument>> GetProvidersConfig()
    {
        try
        {
            var doc = _providerConfigService.GetProvidersConfigForClient();
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(doc));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetProvidersConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_GET_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> SaveProvidersConfig(string configJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configJson))
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        "PROVIDERS_SAVE_INVALID",
                        "Provider 配置参数不能为空"
                    )
                );
            }

            var incoming = JsonSerializer.Deserialize<ProvidersConfigDocument>(
                configJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (incoming == null)
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        "PROVIDERS_SAVE_INVALID",
                        "Provider 配置 JSON 解析失败"
                    )
                );
            }

            var saved = _providerConfigService.SaveProvidersConfig(incoming);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Success(saved, "大模型服务商与模型配置已安全加密落盘")
            );
        }
        catch (ProviderConfigValidationException vex)
        {
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail(
                    "PROVIDER_VALIDATION",
                    $"字段 [{vex.Field}]：{vex.Message}"
                )
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveProvidersConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_SAVE_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> UpsertProvider(string providerJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerJson))
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        "PROVIDER_UPSERT_INVALID",
                        "Provider 参数不能为空"
                    )
                );
            }

            var provider = JsonSerializer.Deserialize<ProviderConfig>(
                providerJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (provider == null)
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        "PROVIDER_UPSERT_INVALID",
                        "Provider JSON 解析失败"
                    )
                );
            }

            var saved = _providerConfigService.UpsertProvider(provider);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Success(saved, $"服务商 [{provider.Name}] 已保存")
            );
        }
        catch (ProviderConfigValidationException vex)
        {
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail(
                    "PROVIDER_VALIDATION",
                    $"字段 [{vex.Field}]：{vex.Message}"
                )
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("UpsertProvider failed", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_UPSERT_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> DeleteProvider(string providerId)
    {
        try
        {
            var saved = _providerConfigService.DeleteProvider(providerId);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Success(saved, "服务商已删除")
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"DeleteProvider failed for '{providerId}'", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_DELETE_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> SetActiveModel(
        string providerId,
        string modelId
    )
    {
        try
        {
            var saved = _providerConfigService.SetActiveModel(providerId, modelId);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Success(saved, $"已切换至模型 {modelId}")
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetActiveModel failed", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail("ACTIVE_MODEL_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<ProviderEndpointPreviewDto>> PreviewProviderResolution(
        string providerJson,
        string? modelId = null,
        string? sessionId = null,
        string? workspacePath = null
    )
    {
        try
        {
            var provider = JsonSerializer.Deserialize<ProviderConfig>(
                providerJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (provider == null)
            {
                return Task.FromResult(
                    BridgeResponse<ProviderEndpointPreviewDto>.Fail(
                        "PREVIEW_INVALID",
                        "Provider JSON 解析失败"
                    )
                );
            }

            var preview = _providerConfigService.PreviewProviderEndpointsAndHeaders(
                provider,
                modelId,
                sessionId,
                workspacePath
            );
            return Task.FromResult(BridgeResponse<ProviderEndpointPreviewDto>.Success(preview));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("PreviewProviderResolution failed", ex);
            return Task.FromResult(
                BridgeResponse<ProviderEndpointPreviewDto>.Fail("PREVIEW_ERROR", ex.Message)
            );
        }
    }

    /// <summary>
    /// 前端发起会话流式请求：
    /// 后台启动 IAsyncEnumerable 管道消费并将事件转换为 SessionStreamEventDto 通过 JsEventDispatcher 推送至前端。
    /// </summary>
}
