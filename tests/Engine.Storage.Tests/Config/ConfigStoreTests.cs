using System.Text.RegularExpressions;
using HaloAgent.Engine.Storage.Config;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Config;

public class ConfigStoreTests
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
                UseProxy = true,
                Models = new() { new ModelConfig { Id = "deepseek-chat", Protocol = "chat", ContextWindow = 65536 } },
            },
        },
    };

    [Fact]
    public void Load_missing_file_returns_default()
    {
        using var dir = new TempDataDir();
        var cfg = ConfigStore.Load();
        Assert.Null(cfg.ActiveProvider);
        Assert.NotNull(cfg.Providers);
    }

    [Fact]
    public void Load_malformed_file_returns_default_instead_of_throwing()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{ this is not json");
        var cfg = ConfigStore.Load();
        Assert.Null(cfg.ActiveProvider);
    }

    [Fact]
    public void Load_old_shape_returns_empty_and_backups_once()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{\"model\":\"m\",\"base_url\":\"http://x/v1\",\"provider\":\"ollama\"}");
        var cfg = ConfigStore.Load();
        Assert.Null(cfg.ActiveProvider);
        Assert.Empty(cfg.Providers);
        var baks = Directory.GetFiles(Path.GetDirectoryName(Paths.ConfigFile)!, "config.json.bak.*");
        Assert.Single(baks);
        // 幂等：不再重复备份
        var cfg2 = ConfigStore.Load();
        Assert.Equal(baks.Length, Directory.GetFiles(Path.GetDirectoryName(Paths.ConfigFile)!, "config.json.bak.*").Length);
    }

    [Fact]
    public void LoadForWrite_old_shape_throws()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{\"model\":\"m\"}");
        Assert.Throws<InvalidOperationException>(() => ConfigStore.LoadForWrite());
    }

    [Fact]
    public void LoadForWrite_force_old_shape_returns_empty_and_backs_up()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{\"model\":\"m\",\"base_url\":\"http://x/v1\"}");
        var cfg = ConfigStore.LoadForWrite(forceOverwriteOldShape: true);
        Assert.Empty(cfg.Providers);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Paths.ConfigFile)!, "config.json.bak.*"));
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        using var dir = new TempDataDir();
        var cfg = NewTree();
        ConfigStore.Save(cfg);
        var back = ConfigStore.Load();
        Assert.Equal("deepseek", back.ActiveProvider);
        Assert.Equal("https://api.deepseek.com/v1", back.Providers["deepseek"].BaseUrl);
    }

    [Fact]
    public void AllProviders_no_merge_returns_user_layer()
    {
        var cfg = NewTree();
        var all = ConfigStore.AllProviders(cfg);
        Assert.True(all.ContainsKey("deepseek"));
        Assert.False(all.ContainsKey("dashscope"));
    }

    [Fact]
    public void Resolve_new_shape_folds_proxy()
    {
        var cfg = NewTree();
        cfg.Proxy.Mode = "manual";
        cfg.Proxy.Url = "http://127.0.0.1:7890";
        var r = ConfigStore.Resolve(cfg);
        Assert.Equal("deepseek", r.ProviderId);
        Assert.Equal("deepseek-chat", r.ModelId);
        Assert.Equal("http://127.0.0.1:7890", r.ProxyUrl);
        Assert.True(r.ProxyInUse);
        // useProxy=false 则直连
        cfg.Providers["deepseek"].UseProxy = false;
        var r2 = ConfigStore.Resolve(cfg);
        Assert.Null(r2.ProxyUrl);
        Assert.False(r2.ProxyInUse);
        Assert.Equal("http://127.0.0.1:7890", r2.GlobalProxyUrl);
    }

    [Fact]
    public void Resolve_auto_is_direct()
    {
        var cfg = NewTree();
        cfg.Proxy.Mode = "auto";
        var r = ConfigStore.Resolve(cfg);
        Assert.Null(r.ProxyUrl);
        Assert.False(r.GlobalProxyAvailable);
    }

    [Fact]
    public void Resolve_unconfigured_throws()
    {
        var cfg = new ConfigModel();
        Assert.Throws<InvalidOperationException>(() => ConfigStore.Resolve(cfg));
    }

    [Fact]
    public void ResolveHeaders_provider_level()
    {
        var cfg = NewTree();
        cfg.Providers["deepseek"].Headers["X-A"] = "1";
        var h = ConfigStore.ResolveHeaders(cfg, "deepseek");
        Assert.Single(h);
        cfg.Providers["deepseek"].HeadersEnabled = false;
        Assert.Empty(ConfigStore.ResolveHeaders(cfg, "deepseek"));
    }

    [Fact]
    public void HeaderTemplate_expands_session_id_and_hash()
    {
        var templates = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("X-Token", "pre-{{session_id}}-{{session_hash}}"),
            new KeyValuePair<string, string>("X-Plain", "abc"),
        };
        var expanded = ConfigStore.HeaderTemplate.ExpandAll(templates, "my-session");
        Assert.Equal(2, expanded.Count);
        var token = expanded[0].Value;
        Assert.Contains("my-session", token, StringComparison.Ordinal);
        Assert.DoesNotContain("{{session_id}}", token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{session_hash}}", token, StringComparison.OrdinalIgnoreCase);
        var hash = ConfigStore.HeaderTemplate.SessionHash("my-session");
        Assert.Matches(new Regex("^[0-9a-f]{16}$"), hash);
        Assert.Contains(hash, token, StringComparison.Ordinal);
        Assert.Equal("abc", expanded[1].Value);
    }

    [Fact]
    public void HeaderTemplate_without_session_skips_templated_headers()
    {
        var templates = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("X-Token", "pre-{{session_id}}"),
            new KeyValuePair<string, string>("X-Plain", "abc"),
        };
        var expanded = ConfigStore.HeaderTemplate.ExpandAll(templates, null);
        Assert.Single(expanded);
        Assert.Equal("X-Plain", expanded[0].Key);
    }

    [Fact]
    public void ValidateTree_matrix()
    {
        var cfg = NewTree();
        Assert.Empty(ConfigValidation.ValidateTree(cfg));
        cfg.Providers["bad id"] = new ProviderConfig { BaseUrl = "https://x.local/v1" };
        Assert.NotEmpty(ConfigValidation.ValidateTree(cfg));
    }
}
