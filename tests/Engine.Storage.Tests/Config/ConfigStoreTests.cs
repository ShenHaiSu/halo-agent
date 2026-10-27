using System.Text.RegularExpressions;
using HaloAgent.Engine.Storage.Config;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Config;

public class ConfigStoreTests
{
    [Fact]
    public void Load_missing_file_returns_default()
    {
        using var dir = new TempDataDir();

        var cfg = ConfigStore.Load();

        Assert.Null(cfg.Model);
        Assert.NotNull(cfg.Providers);
        Assert.NotNull(cfg.ExtraHeaders);
    }

    [Fact]
    public void Load_malformed_file_returns_default_instead_of_throwing()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{ this is not json");

        var cfg = ConfigStore.Load();

        Assert.Null(cfg.Model);
    }

    [Fact]
    public void LoadForWrite_missing_file_returns_default_without_throwing()
    {
        using var dir = new TempDataDir();

        var cfg = ConfigStore.LoadForWrite();

        Assert.NotNull(cfg);
        Assert.Null(cfg.Model);
    }

    [Fact]
    public void LoadForWrite_malformed_file_throws_to_avoid_overwrite()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.ConfigFile, "{ this is not json");

        Assert.Throws<InvalidOperationException>(() => ConfigStore.LoadForWrite());
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        using var dir = new TempDataDir();
        var cfg = new ConfigModel { Model = "m", BaseUrl = "http://x/v1" };

        ConfigStore.Save(cfg);
        var back = ConfigStore.Load();

        Assert.Equal("m", back.Model);
        Assert.Equal("http://x/v1", back.BaseUrl);
    }

    [Fact]
    public void AllProviders_merges_user_fields_without_replacing_builtin()
    {
        using var dir = new TempDataDir();
        var cfg = new ConfigModel();
        cfg.Providers["dashscope"] = new ProviderDef { Model = "custom-model" };
        cfg.Providers["mine"] = new ProviderDef { BaseUrl = "http://mine/v1", Model = "mm" };

        var all = ConfigStore.AllProviders(cfg);

        Assert.Equal("custom-model", all["dashscope"].Model);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", all["dashscope"].BaseUrl);
        Assert.Equal("http://mine/v1", all["mine"].BaseUrl);
    }

    [Fact]
    public void ContextWindows_unknown_model_returns_null()
    {
        Assert.Null(ContextWindows.ForModel("totally-unknown-model-zzz"));
        Assert.Equal(65536, ContextWindows.ForModel("deepseek-chat"));
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
    public void ResolveHeaders_master_switch_off_sends_nothing()
    {
        using var dir = new TempDataDir();
        var cfg = new ConfigModel
        {
            ExtraHeadersEnabled = false,
            ExtraHeaders = new Dictionary<string, string> { ["X-A"] = "1" },
        };
        cfg.Providers["p1"] = new ProviderDef
        {
            ExtraHeaders = new Dictionary<string, string> { ["X-B"] = "2" },
        };

        var headers = ConfigStore.ResolveHeaders(cfg, "p1");

        Assert.Empty(headers);
    }
}
