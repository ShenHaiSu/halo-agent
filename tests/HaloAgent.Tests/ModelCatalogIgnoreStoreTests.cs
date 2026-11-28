using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 忽视表与目录桥只读单测：原子写读回、损坏回空、大小写去重、删服务商清键。
/// </summary>
public sealed class ModelCatalogIgnoreStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapFile;

    public ModelCatalogIgnoreStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloModelCatalog_{Guid.NewGuid():N}");
        _bootstrapFile = Path.Combine(
            Path.GetTempPath(),
            $"HaloModelCatalogBootstrap_{Guid.NewGuid():N}.json"
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

    private ModelCatalogIgnoreStore CreateStore()
    {
        var storage = StoragePathManager.Initialize(
            _bootstrapFile,
            Path.Combine(_tempRoot, "HaloAgentData"),
            Path.Combine(_tempRoot, "HaloAgentDataFallback")
        );
        return new ModelCatalogIgnoreStore(storage);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundtripTrimmedIds()
    {
        var store = CreateStore();

        store.Save("provider-demo", new[] { "  glm-5.3 ", "", "kimi-k3", "glm-5.3" });

        var loaded = store.Load("provider-demo");
        Assert.Equal(2, loaded.Count);
        Assert.Contains("glm-5.3", loaded);
        Assert.Contains("kimi-k3", loaded);
    }

    [Fact]
    public void Load_CorruptedFile_ShouldReturnEmpty()
    {
        var store = CreateStore();
        store.Save("provider-demo", new[] { "glm-5.3" });
        File.WriteAllText(store.IgnoreFilePath, "{ not-json");

        Assert.Empty(store.Load("provider-demo"));
    }

    [Fact]
    public void RemoveProvider_ShouldClearKey()
    {
        var store = CreateStore();
        store.Save("provider-demo", new[] { "glm-5.3" });

        store.RemoveProvider("provider-demo");

        Assert.Empty(store.Load("provider-demo"));
    }
}

/// <summary>
/// 目录桥只读单测：空参判请求非法、未知服务商判不存在。
/// </summary>
public sealed class ProviderModelCatalogBridgeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapFile;

    public ProviderModelCatalogBridgeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloCatalogBridge_{Guid.NewGuid():N}");
        _bootstrapFile = Path.Combine(
            Path.GetTempPath(),
            $"HaloCatalogBridgeBootstrap_{Guid.NewGuid():N}.json"
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
    public async Task FetchProviderModels_EmptyId_ShouldReturnRequestInvalid()
    {
        var bridge = CreateBridge();

        var res = await bridge.FetchProviderModels("  ");

        Assert.False(res.Ok);
        Assert.Equal(CatalogErrorCodes.RequestInvalid, res.Code);
    }

    [Fact]
    public async Task FetchProviderModels_UnknownProvider_ShouldReturnNotFound()
    {
        var bridge = CreateBridge();

        var res = await bridge.FetchProviderModels("provider-missing");

        Assert.False(res.Ok);
        Assert.Equal(CatalogErrorCodes.ProviderNotFound, res.Code);
    }
}
