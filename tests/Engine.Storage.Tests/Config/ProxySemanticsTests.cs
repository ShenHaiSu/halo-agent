using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Storage.Config;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Config;

/// <summary>9 格真值表 + 非法组合 + 迁移映射 + 本地旁路。</summary>
public class ProxySemanticsTests
{
    private static ProxyConfig G(string? mode, string? url = null) => new() { Mode = mode, Url = url };
    private const string ValidUrl = "http://127.0.0.1:7890";

    [Theory]
    [InlineData("direct", "follow", ProxyKind.None, "")]
    [InlineData("direct", "direct", ProxyKind.None, "")]
    [InlineData("manual", "follow", ProxyKind.Manual, "")]
    [InlineData("manual", "direct", ProxyKind.None, "")]
    [InlineData("manual", "proxy", ProxyKind.Manual, "")]
    [InlineData("system", "follow", ProxyKind.System, "")]
    [InlineData("system", "direct", ProxyKind.None, "")]
    [InlineData("system", "proxy", ProxyKind.System, "")]
    public void TruthTable_9grid(string mode, string network, ProxyKind kind, string _)
    {
        var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(G(mode, ValidUrl), network);
        Assert.Equal(kind, eff.Kind);
        if (kind == ProxyKind.Manual) Assert.Equal(ValidUrl, eff.Url);
        else Assert.Null(eff.Url);
        if (mode == "direct" && network == "proxy")
            Assert.Equal(ConfigStore.ProxyDowngradeWarning, warn);
        else
            Assert.Equal("", warn);
    }

    [Fact]
    public void ProxyDirect_GlobalDirect_WarnsAndFallsBack()
    {
        var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(G("direct"), "proxy");
        Assert.Equal(ProxyKind.None, eff.Kind);
        Assert.Equal(ConfigStore.ProxyDowngradeWarning, warn);
    }

    [Fact]
    public void ManualMissingUrl_FallsBackWithWarning()
    {
        var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(G("manual", null), "follow");
        Assert.Equal(ProxyKind.None, eff.Kind);
        Assert.Equal(ConfigStore.ProxyInvalidUrlWarning, warn);
    }

    [Fact]
    public void ManualIllegalUrl_FallsBackWithWarning()
    {
        var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(G("manual", "socks5://x:1"), "proxy");
        Assert.Equal(ProxyKind.None, eff.Kind);
        Assert.NotEqual("", warn);
    }

    [Fact]
    public void UnknownEnums_FallBackConservative()
    {
        var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(G("weird"), "follow");
        Assert.Equal(ProxyKind.None, eff.Kind); // 未知全局→direct
        var (eff2, _) = ConfigStore.TryResolveEffectiveProxy(G("system"), "weird");
        Assert.Equal(ProxyKind.System, eff2.Kind); // 未知 network→follow
        Assert.Equal("", warn);
    }

    [Fact]
    public void LegacyAuto_Custom_Normalize()
    {
        Assert.Equal("direct", ConfigStore.NormalizeGlobalMode("auto"));
        Assert.Equal("manual", ConfigStore.NormalizeGlobalMode("custom"));
        Assert.Equal("direct", ConfigStore.NormalizeGlobalMode("unknown"));
    }

    [Fact]
    public void NormalizeConfig_NewFile_System_OldFile_Direct()
    {
        var fresh = new ConfigModel();
        ConfigStore.NormalizeConfig(fresh, isNewFile: true);
        Assert.Equal("system", fresh.Proxy.Mode);
        var old = new ConfigModel();
        ConfigStore.NormalizeConfig(old, isNewFile: false);
        Assert.Equal("direct", old.Proxy.Mode);
        var legacy = new ConfigModel();
        legacy.Proxy.Mode = "auto";
        ConfigStore.NormalizeConfig(legacy, isNewFile: false);
        Assert.Equal("direct", legacy.Proxy.Mode);
    }

    [Fact]
    public void OldUseProxy_Compat()
    {
        var p = new ProviderConfig { Network = "follow", UseProxyLegacy = false };
        // 空 network + use_proxy=false → direct
        p.Network = "";
        Assert.Equal("direct", ConfigStore.NormalizeProviderNetwork(p.Network, p.UseProxyLegacy));
        p.UseProxyLegacy = true;
        Assert.Equal("follow", ConfigStore.NormalizeProviderNetwork("", p.UseProxyLegacy));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("foo.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.167.1.1", false)]
    [InlineData("printer.local", true)]
    [InlineData("example.com", false)]
    [InlineData("127.0.0.1:7890", true)]
    [InlineData("localhost.", true)]
    public void BypassTable(string host, bool expected)
    {
        Assert.Equal(expected, ConfigStore.IsBypassedHost(host));
    }

    [Fact]
    public void Resolve_LocalBaseUrl_AlwaysDirect()
    {
        var cfg = new ConfigModel
        {
            ActiveProvider = "o",
            ActiveModel = "m",
            Proxy = G("manual", ValidUrl),
            Providers =
            {
                ["o"] = new ProviderConfig
                {
                    BaseUrl = "http://localhost:11434/v1",
                    Network = "proxy",
                    Models = new() { new ModelConfig { Id = "m" } },
                },
            },
        };
        var r = ConfigStore.Resolve(cfg);
        Assert.Equal(ProxyKind.None, r.ProxyKind);
        Assert.False(r.ProxyInUse);
        Assert.Equal(ProxySources.BypassLocal, r.ProxySource);
    }

    [Fact]
    public void Resolve_FillsKindSourceWarning()
    {
        var cfg = new ConfigModel
        {
            ActiveProvider = "o",
            ActiveModel = "m",
            Proxy = G("direct"),
            Providers =
            {
                ["o"] = new ProviderConfig
                {
                    BaseUrl = "https://api.example.com/v1",
                    Network = "proxy",
                    Models = new() { new ModelConfig { Id = "m" } },
                },
            },
        };
        var r = ConfigStore.Resolve(cfg);
        Assert.Equal(ProxyKind.None, r.ProxyKind);
        Assert.Equal(ConfigStore.ProxyDowngradeWarning, r.ProxyWarning);
        Assert.Equal(ProxySources.ProviderProxy, r.ProxySource);
    }

    [Fact]
    public void Validation_Warning_MatchesTryResolve()
    {
        var cfg = new ConfigModel
        {
            Proxy = G("direct"),
            Providers =
            {
                ["o"] = new ProviderConfig
                {
                    BaseUrl = "https://api.example.com/v1",
                    Network = "proxy",
                    Models = new() { new ModelConfig { Id = "m" } },
                },
            },
        };
        var ws = ConfigValidation.Warnings(cfg);
        Assert.Contains(ConfigStore.ProxyDowngradeWarning, ws);
    }
}
