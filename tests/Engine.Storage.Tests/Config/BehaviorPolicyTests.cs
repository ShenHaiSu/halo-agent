using HaloAgent.Engine.Storage.Config;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Config;

/// <summary>行为页：Resolve 缺省/越界 + ValidateBehavior 边界。</summary>
public sealed class BehaviorPolicyTests
{
    private static ConfigModel NewTree() => new()
    {
        ActiveProvider = "deepseek",
        ActiveModel = "deepseek-chat",
        Providers =
        {
            ["deepseek"] = new ProviderConfig
            {
                BaseUrl = "https://api.deepseek.com/v1",
                Network = "follow",
                Models = new() { new ModelConfig { Id = "deepseek-chat", Protocol = "chat", ContextWindow = 65536 } },
            },
        },
    };

    [Fact]
    public void Resolve_defaults_to_200_0_empty_2_false()
    {
        using var dir = new TempDataDir();
        var resolved = ConfigStore.Resolve(NewTree());
        Assert.Equal(200, resolved.MaxTurns);
        Assert.Equal(0, resolved.Retry);
        Assert.Equal("", resolved.Persona);
        Assert.Equal(2, resolved.CompactKeepTail);
        Assert.False(resolved.AutoCompactOnLimit);
    }

    [Fact]
    public void Resolve_clamps_out_of_range()
    {
        using var dir = new TempDataDir();
        var cfg = NewTree();
        cfg.MaxTurns = 600;
        cfg.Retry = 99;
        cfg.CompactKeepTail = 0;
        cfg.AutoCompactOnLimit = true;
        var resolved = ConfigStore.Resolve(cfg);
        Assert.Equal(500, resolved.MaxTurns);
        Assert.Equal(8, resolved.Retry);
        Assert.Equal(1, resolved.CompactKeepTail);
        Assert.True(resolved.AutoCompactOnLimit);
    }

    [Fact]
    public void Resolve_maxTurns_zero_kept()
    {
        using var dir = new TempDataDir();
        var cfg = NewTree();
        cfg.MaxTurns = 0;
        var resolved = ConfigStore.Resolve(cfg);
        Assert.Equal(0, resolved.MaxTurns);
    }

    [Fact]
    public void ValidateBehavior_rejects_out_of_range()
    {
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(-1, null, null, null, null, _ => true));
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(501, null, null, null, null, _ => true));
        Assert.Empty(ConfigValidation.ValidateBehaviorValues(0, null, null, null, null, _ => true));
        Assert.Empty(ConfigValidation.ValidateBehaviorValues(500, null, null, null, null, _ => true));
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, 9, null, null, null, _ => true));
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, null, null, 0, null, _ => true));
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, null, null, 11, null, _ => true));
    }

    [Fact]
    public void ValidateBehavior_persona_format_and_existence()
    {
        // 空串通过
        Assert.Empty(ConfigValidation.ValidateBehaviorValues(null, null, "", null, null, _ => false));
        // 超长拒
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, null, new string('a', 65), null, null, _ => true));
        // 非法字符拒
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, null, "a/b", null, null, _ => true));
        // 不存在拒（predicate=false）
        Assert.NotEmpty(ConfigValidation.ValidateBehaviorValues(null, null, "不存在的人格xyz", null, null, _ => false));
        // 存在过（predicate=true）
        Assert.Empty(ConfigValidation.ValidateBehaviorValues(null, null, "毒舌损友", null, null, _ => true));
        // 无 predicate 时只查格式，不查存在性
        Assert.Empty(ConfigValidation.ValidateBehaviorValues(null, null, "任意名", null, null, null));
    }

    [Fact]
    public void ValidateTree_blocks_bad_behavior()
    {
        using var dir = new TempDataDir();
        var cfg = NewTree();
        cfg.MaxTurns = 501;
        Assert.Contains(ConfigValidation.ValidateTree(cfg), e => e.Field == "maxTurns");
    }
}
