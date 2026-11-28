using Host.Desktop.Core.Models;
using Host.Desktop.ModelCatalog;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 快照差分单测：新增 / 已存在 / 已忽视三态，忽视优先，大小写折叠。
/// </summary>
public sealed class ProviderModelDiffTests
{
    private static List<DiscoveredProviderModel> Snapshot(params string[] ids)
    {
        return ids.Select(id => new DiscoveredProviderModel { Id = id }).ToList();
    }

    [Fact]
    public void ApplyDiff_ShouldSplitThreeStates()
    {
        var discovered = Snapshot("glm-new", "glm-keep", "glm-hide");
        var existing = new List<ModelConfig>
        {
            new() { Id = "glm-keep", Enabled = true },
            new() { Id = "glm-gone", Enabled = true }
        };
        var ignored = new List<string> { "glm-hide" };

        ProviderModelDiffCalculator.ApplyDiff(discovered, existing, ignored);

        Assert.Equal(CatalogModelState.New, discovered[0].State);
        Assert.False(discovered[0].ExistsEnabled);
        Assert.Equal(CatalogModelState.Existing, discovered[1].State);
        Assert.True(discovered[1].ExistsEnabled);
        Assert.Equal(CatalogModelState.Ignored, discovered[2].State);
        Assert.False(discovered[2].ExistsEnabled);
    }

    [Fact]
    public void ApplyDiff_IgnoredShouldWinOverExisting()
    {
        var discovered = Snapshot("glm-5.3");
        var existing = new List<ModelConfig>
        {
            new() { Id = "glm-5.3", Enabled = true }
        };

        ProviderModelDiffCalculator.ApplyDiff(
            discovered,
            existing,
            new List<string> { " glm-5.3 " }
        );

        Assert.Equal(CatalogModelState.Ignored, discovered[0].State);
        Assert.False(discovered[0].ExistsEnabled);
    }

    [Fact]
    public void ApplyDiff_ShouldFoldCaseAndFillEnabled()
    {
        var discovered = Snapshot("GLM-5.3", "glm-off");
        var existing = new List<ModelConfig>
        {
            new() { Id = "glm-5.3", Enabled = false },
            new() { Id = "GLM-OFF", Enabled = true }
        };

        ProviderModelDiffCalculator.ApplyDiff(discovered, existing, null);

        Assert.Equal(CatalogModelState.Existing, discovered[0].State);
        Assert.False(discovered[0].ExistsEnabled);
        Assert.Equal(CatalogModelState.Existing, discovered[1].State);
        Assert.True(discovered[1].ExistsEnabled);
    }
}
