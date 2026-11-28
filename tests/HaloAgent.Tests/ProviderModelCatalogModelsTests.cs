using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 供应商模型目录契约单测：DTO 小驼峰 + 错误码与设计错误码表对齐。
/// </summary>
public sealed class ProviderModelCatalogModelsTests
{
    [Fact]
    public void Serialize_Result_ShouldUseCamelCase()
    {
        var result = new FetchProviderModelsResult
        {
            ProviderId = "provider-demo",
            ModelsUrl = "https://api.openai.com/v1/models",
            EffectiveProxySummary = "DIRECT (No Proxy)",
            ElapsedMs = 12,
            Models = new List<DiscoveredProviderModel>
            {
                new()
                {
                    Id = "glm-5.3",
                    OwnedBy = "openai",
                    Created = 1626777600,
                    State = CatalogModelState.New,
                    ExistsEnabled = false
                }
            }
        };

        string json = JsonSerializer.Serialize(result, HaloJsonOptions.IndentedCamelCase);

        Assert.Contains("\"providerId\"", json);
        Assert.Contains("\"modelsUrl\"", json);
        Assert.Contains("\"effectiveProxySummary\"", json);
        Assert.Contains("\"elapsedMs\"", json);
        Assert.Contains("\"ownedBy\"", json);
        Assert.Contains("\"existsEnabled\"", json);
    }

    [Fact]
    public void ErrorCodes_ShouldMatchCatalogContract()
    {
        Assert.Equal("CATALOG_PROVIDER_NOT_FOUND", CatalogErrorCodes.ProviderNotFound);
        Assert.Equal("CATALOG_MISSING_API_KEY", CatalogErrorCodes.MissingApiKey);
        Assert.Equal("CATALOG_UPSTREAM_401_403", CatalogErrorCodes.UpstreamAuth);
        Assert.Equal("CATALOG_UPSTREAM_404", CatalogErrorCodes.UpstreamNotFound);
        Assert.Equal("CATALOG_UPSTREAM_5XX", CatalogErrorCodes.UpstreamError);
        Assert.Equal("CATALOG_PARSE_ERROR", CatalogErrorCodes.ParseError);
        Assert.Equal("CATALOG_NETWORK_ERROR", CatalogErrorCodes.NetworkError);
        Assert.Equal("CATALOG_TIMEOUT", CatalogErrorCodes.Timeout);
        Assert.Equal("CATALOG_REQUEST_INVALID", CatalogErrorCodes.RequestInvalid);
    }

    [Fact]
    public void Deserialize_UnknownState_ShouldFallBackToNew()
    {
        var model = JsonSerializer.Deserialize<DiscoveredProviderModel>(
            "{\"id\":\"glm-5.3\",\"state\":\"unknown-state\"}",
            HaloJsonOptions.IndentedCamelCase
        );

        Assert.NotNull(model);
        Assert.Equal("glm-5.3", model!.Id);
        Assert.Equal(CatalogModelState.New, model.State);
    }
}
