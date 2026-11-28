using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.ModelCatalog;

namespace Host.Desktop.Bridge;

/// <summary>
/// 供应商模型目录桥（薄路由）：只做参数校验、错误码映射与事件发布，不写业务。
/// </summary>
public sealed partial class HostBridgeFacade
{
    private ProviderModelCatalogService ResolveCatalogService()
    {
        if (_modelCatalogService != null)
        {
            return _modelCatalogService;
        }

        if (_providerConfigService == null)
        {
            throw new ProviderModelCatalogException(CatalogErrorCodes.RequestInvalid, "服务商配置服务未就绪");
        }

        _modelCatalogIgnoreStore ??= new ModelCatalogIgnoreStore(_storageManager);
        _modelCatalogService ??= new ProviderModelCatalogService(
            _providerConfigService,
            _httpClientFactory,
            _modelCatalogIgnoreStore
        );
        return _modelCatalogService;
    }

    public async Task<BridgeResponse<FetchProviderModelsResult>> FetchProviderModels(
        string? providerId,
        CancellationToken ct = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return BridgeResponse<FetchProviderModelsResult>.Fail(
                    CatalogErrorCodes.RequestInvalid,
                    "服务商 Id 不能为空"
                );
            }

            var service = ResolveCatalogService();
            var result = await service.FetchAsync(providerId.Trim(), ct).ConfigureAwait(false);
            return BridgeResponse<FetchProviderModelsResult>.Success(result);
        }
        catch (ProviderModelCatalogException vex)
        {
            return BridgeResponse<FetchProviderModelsResult>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("FetchProviderModels failed", ex);
            return BridgeResponse<FetchProviderModelsResult>.Fail(
                CatalogErrorCodes.NetworkError,
                ex.Message
            );
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> ImportDiscoveredModels(string? requestJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        CatalogErrorCodes.RequestInvalid,
                        "导入参数不能为空"
                    )
                );
            }

            var request = JsonSerializer.Deserialize<ImportDiscoveredModelsRequest>(
                requestJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (request == null)
            {
                return Task.FromResult(
                    BridgeResponse<ProvidersConfigDocument>.Fail(
                        CatalogErrorCodes.RequestInvalid,
                        "导入参数解析失败"
                    )
                );
            }

            var service = ResolveCatalogService();
            var saved = service.Import(request);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Success(saved, "已批量引入供应商模型")
            );
        }
        catch (ProviderModelCatalogException vex)
        {
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail(vex.Code, vex.Message)
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
            HaloLogger.App.Error("ImportDiscoveredModels failed", ex);
            return Task.FromResult(
                BridgeResponse<ProvidersConfigDocument>.Fail(
                    CatalogErrorCodes.RequestInvalid,
                    ex.Message
                )
            );
        }
    }

    public Task<BridgeResponse<bool>> SetDiscoveredModelsIgnored(
        string? providerId,
        string? modelIdsJson,
        bool ignored
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return Task.FromResult(
                    BridgeResponse<bool>.Fail(CatalogErrorCodes.RequestInvalid, "服务商 Id 不能为空")
                );
            }

            List<string>? modelIds = null;
            if (!string.IsNullOrWhiteSpace(modelIdsJson))
            {
                modelIds = JsonSerializer.Deserialize<List<string>>(
                    modelIdsJson,
                    HaloJsonOptions.IndentedCamelCase
                );
            }

            var service = ResolveCatalogService();
            var saved = service.SetIgnored(providerId.Trim(), modelIds, ignored);
            JsEventDispatcher.Publish(
                "config:model-catalog-changed",
                new { providerId = providerId.Trim(), ignored = saved.ToArray() }
            );
            return Task.FromResult(BridgeResponse<bool>.Success(true));
        }
        catch (ProviderModelCatalogException vex)
        {
            return Task.FromResult(BridgeResponse<bool>.Fail(vex.Code, vex.Message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetDiscoveredModelsIgnored failed", ex);
            return Task.FromResult(
                BridgeResponse<bool>.Fail(CatalogErrorCodes.RequestInvalid, ex.Message)
            );
        }
    }
}
