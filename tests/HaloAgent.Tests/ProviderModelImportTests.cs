using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.ModelCatalog;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 模型导入单测：双开关、激活翻转、导入清忽视、非法 Id 透传校验。
/// </summary>
public sealed class ProviderModelImportTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapFile;

    public ProviderModelImportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloCatalogImport_{Guid.NewGuid():N}");
        _bootstrapFile = Path.Combine(
            Path.GetTempPath(),
            $"HaloCatalogImportBootstrap_{Guid.NewGuid():N}.json"
        );
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            if (File.Exists(_bootstrapFile))
            {
                File.Delete(_bootstrapFile);
            }
        }
        catch
        {
            // 忽略临时目录清理竞态
        }
    }

    private (ProviderModelCatalogService service, string providerId) CreateService()
    {
        var storage = StoragePathManager.Initialize(
            _bootstrapFile,
            Path.Combine(_tempRoot, "HaloAgentData"),
            Path.Combine(_tempRoot, "HaloAgentDataFallback")
        );
        var proxyHub = new HaloHttpClientFactory();
        var providerConfigService = new ProviderConfigService(storage, proxyHub);
        var ignoreStore = new ModelCatalogIgnoreStore(storage);
        var service = new ProviderModelCatalogService(providerConfigService, proxyHub, ignoreStore);

        var saved = providerConfigService.UpsertProvider(
            new ProviderConfig
            {
                Id = "provider-demo",
                Name = "Demo",
                BaseUrl = "https://api.openai.com/v1",
                Models = new List<ModelConfig>
                {
                    new()
                    {
                        Id = "glm-keep",
                        DisplayName = "glm-keep",
                        Enabled = false
                    }
                }
            }
        );
        string providerId = saved.Providers[0].Id;
        return (service, providerId);
    }

    [Fact]
    public void Import_DisabledFalse_ShouldEnableAndAppend()
    {
        var (service, providerId) = CreateService();

        var saved = service.Import(
            new ImportDiscoveredModelsRequest
            {
                ProviderId = providerId,
                ModelIds = new List<string> { "glm-keep", " glm-new " },
                ImportDisabled = false
            }
        );

        var provider = saved.Providers.First(p => p.Id == providerId);
        var keep = provider.Models.First(m => m.Id == "glm-keep");
        var added = provider.Models.First(m => m.Id == "glm-new");
        Assert.True(keep.Enabled);
        Assert.True(added.Enabled);
        Assert.Equal("glm-new", added.DisplayName);
    }

    [Fact]
    public void Import_DisabledTrue_ShouldAppendDisabledAndClearIgnored()
    {
        var (service, providerId) = CreateService();
        service.SetIgnored(providerId, new[] { "glm-new" }, true);

        var saved = service.Import(
            new ImportDiscoveredModelsRequest
            {
                ProviderId = providerId,
                ModelIds = new List<string> { "glm-new" },
                ImportDisabled = true
            }
        );

        var provider = saved.Providers.First(p => p.Id == providerId);
        var added = provider.Models.First(m => m.Id == "glm-new");
        Assert.False(added.Enabled);

        var ignored = service.SetIgnored(providerId, null, true);
        Assert.DoesNotContain("glm-new", ignored);
    }

    [Fact]
    public void SetIgnored_ShouldRoundtrip()
    {
        var (service, providerId) = CreateService();

        var ignored = service.SetIgnored(providerId, new[] { " glm-hide ", "" }, true);
        Assert.Contains("glm-hide", ignored);

        var cleared = service.SetIgnored(providerId, new[] { "GLM-HIDE" }, false);
        Assert.DoesNotContain("glm-hide", cleared);
    }
}

/// <summary>
/// 目录桥写方法单测：空参判请求非法、未知服务商判不存在。
/// </summary>
public sealed class ProviderModelCatalogWriteBridgeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapFile;

    public ProviderModelCatalogWriteBridgeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloCatalogWrite_{Guid.NewGuid():N}");
        _bootstrapFile = Path.Combine(
            Path.GetTempPath(),
            $"HaloCatalogWriteBootstrap_{Guid.NewGuid():N}.json"
        );
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            if (File.Exists(_bootstrapFile))
            {
                File.Delete(_bootstrapFile);
            }
        }
        catch
        {
            // 忽略临时目录清理竞态
        }
    }

    private HostBridgeFacade CreateBridge()
    {
        var storage = StoragePathManager.Initialize(
            _bootstrapFile,
            Path.Combine(_tempRoot, "HaloAgentData"),
            Path.Combine(_tempRoot, "HaloAgentDataFallback")
        );
        var proxyHub = new HaloHttpClientFactory();
        var providerConfigService = new ProviderConfigService(storage, proxyHub);
        return new HostBridgeFacade(
            mainForm: null,
            storageManager: storage,
            providerConfigService: providerConfigService,
            httpClientFactory: proxyHub
        );
    }

    [Fact]
    public async Task ImportDiscoveredModels_EmptyJson_ShouldReturnRequestInvalid()
    {
        var bridge = CreateBridge();

        var res = await bridge.ImportDiscoveredModels("  ");

        Assert.False(res.Ok);
        Assert.Equal(CatalogErrorCodes.RequestInvalid, res.Code);
    }

    [Fact]
    public async Task SetIgnored_UnknownProvider_ShouldReturnNotFound()
    {
        var bridge = CreateBridge();

        var res = await bridge.SetDiscoveredModelsIgnored(
            "provider-missing",
            "[\"glm-5.3\"]",
            true
        );

        Assert.False(res.Ok);
        Assert.Equal(CatalogErrorCodes.ProviderNotFound, res.Code);
    }
}
