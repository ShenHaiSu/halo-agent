#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-03/08 §1：模型视觉能力位兼容（老文件回退 + 超限拒绝 + Clone 不丢）。</summary>
public sealed class Stage09VisionModelTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;

    public Stage09VisionModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09Vision_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09VisionBs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot)) Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    private ProviderConfigService NewService()
    {
        var storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);
        return new ProviderConfigService(storage, new HaloHttpClientFactory());
    }

    [Fact(DisplayName = "S09-V1: 老 providers.json 缺字段回退 false/4/high")]
    public void LegacyModelJson_FallsBackToVisionDefaults()
    {
        string legacy = "{\"id\":\"gpt-4o\",\"displayName\":\"GPT\",\"protocol\":\"chat\",\"maxContextTokens\":128000,\"maxOutputTokens\":4096,\"enabled\":true}";
        var model = JsonSerializer.Deserialize<ModelConfig>(legacy, HaloJsonOptions.IndentedCamelCase);
        Assert.NotNull(model);
        Assert.False(model!.SupportsVision);
        Assert.Equal(4, model.MaxImagesPerTurn);
        Assert.Equal("high", model.VisionDetail);
        Assert.Equal(4, VisionDefaults.ResolveMaxImages(0));
    }

    [Fact(DisplayName = "S09-V2: MaxImagesPerTurn 越界拒绝 PROVIDER_VALIDATION")]
    public void MaxImages_OutOfRange_Rejected()
    {
        var svc = NewService();
        var doc = svc.GetProvidersConfigInternal();
        var provider = doc.Providers[0];
        provider.Models.Add(new ModelConfig { Id = "bad-model", MaxImagesPerTurn = 17 });
        var ex = Assert.Throws<ProviderConfigValidationException>(() => svc.SaveProvidersConfig(doc));
        Assert.Equal("PROVIDER_VALIDATION", ex.Code);
        Assert.Equal("maxImagesPerTurn", ex.Field);

        var doc2 = svc.GetProvidersConfigInternal();
        var provider2 = doc2.Providers[0];
        provider2.Models.Add(new ModelConfig { Id = "bad-model2", MaxImagesPerTurn = -1 });
        Assert.Throws<ProviderConfigValidationException>(() => svc.UpsertProvider(provider2));
    }

    [Fact(DisplayName = "S09-V3: 边界值 0 与 16 通过，非法 detail 回退 high")]
    public void BoundaryValues_Accepted_DetailNormalized()
    {
        var svc = NewService();
        var doc = svc.GetProvidersConfigInternal();
        var provider = doc.Providers[0];
        provider.Models.Add(new ModelConfig
        {
            Id = "vision-model",
            SupportsVision = true,
            MaxImagesPerTurn = 0,
            VisionDetail = "ULTRA",
        });
        var saved = svc.SaveProvidersConfig(doc);
        var round = saved.Providers[0].Models.Find(m => m.Id == "vision-model");
        Assert.NotNull(round);
        Assert.True(round!.SupportsVision);
        Assert.Equal(0, round.MaxImagesPerTurn);
        Assert.Equal("high", round.VisionDetail);
        Assert.Equal(4, VisionDefaults.ResolveMaxImages(round.MaxImagesPerTurn));
    }

    [Fact(DisplayName = "S09-V4: Clone 不丢能力位")]
    public void Clone_PreservesVisionFlags()
    {
        var src = new ModelConfig
        {
            Id = "m", SupportsVision = true, MaxImagesPerTurn = 9, VisionDetail = "high",
        };
        var clone = src.Clone();
        Assert.True(clone.SupportsVision);
        Assert.Equal(9, clone.MaxImagesPerTurn);
        Assert.Equal("high", clone.VisionDetail);
        var provider = new ProviderConfig { Id = "p", Name = "P", Models = new() { src } };
        var pclone = provider.Clone();
        Assert.True(pclone.Models[0].SupportsVision);
        Assert.Equal(9, pclone.Models[0].MaxImagesPerTurn);
    }

    [Fact(DisplayName = "S09-V5: detail 大小写归一化")]
    public void Detail_Normalized_CaseInsensitive()
    {
        Assert.Equal("low", VisionDefaults.NormalizeDetail("LOW"));
        Assert.Equal("high", VisionDefaults.NormalizeDetail(" High "));
        Assert.Equal("auto", VisionDefaults.NormalizeDetail("auto"));
        Assert.Equal("high", VisionDefaults.NormalizeDetail(null));
        Assert.Equal("high", VisionDefaults.NormalizeDetail("4k"));
    }
}
