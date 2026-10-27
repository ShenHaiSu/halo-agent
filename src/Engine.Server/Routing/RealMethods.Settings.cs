using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;

namespace HaloAgent.Engine.Server.Routing;

public sealed partial class RealMethods
{
    public Task<object?> SettingsGetAsync(JsonElement? p, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();

        ResolvedConfig resolved;
        try { resolved = ConfigStore.Resolve(cfg); }
        catch (InvalidOperationException)
        {
            resolved = new ResolvedConfig
            {
                Model = cfg.Model ?? "",
                BaseUrl = cfg.BaseUrl ?? "",
                ProviderName = cfg.Provider ?? "ollama",
                ApiKey = cfg.ApiKey,
                ContextWindow = cfg.ContextWindow,
                MaxTurns = cfg.MaxTurns ?? 200,
                Retry = cfg.Retry ?? 0,
            };
        }

        var providers = ConfigStore.AllProviders(cfg)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ProviderEntry
            {
                Name = kv.Key,
                Model = kv.Value.Model ?? "",
                BaseUrl = kv.Value.BaseUrl ?? "",
            })
            .ToList();

        var headers = (cfg.ExtraHeaders ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new HeaderPair { Name = kv.Key, Value = kv.Value })
            .ToList();

        var mode = (cfg.Proxy.Mode ?? "auto").ToLowerInvariant();
        if (mode is not ("auto" or "direct" or "manual")) mode = "auto";

        var network = new SettingsGetNetwork
        {
            Mode = mode,
            Url = cfg.Proxy.Url,
            Retry = cfg.Retry ?? 0,
            HeadersEnabled = cfg.ExtraHeadersEnabled ?? true,
            Headers = headers,
            ProviderHeaders = (cfg.Providers ?? new Dictionary<string, ProviderDef>())
                .Where(kv => kv.Value.ExtraHeaders.Count > 0)
                .ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<HeaderPair>)kv.Value.ExtraHeaders
                        .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(h => new HeaderPair { Name = h.Key, Value = h.Value })
                        .ToList()),
        };

        return Task.FromResult<object?>(new SettingsGetResult
        {
            Model = resolved.Model,
            Provider = resolved.ProviderName,
            BaseUrl = resolved.BaseUrl,
            ApiKeySet = !string.IsNullOrWhiteSpace(resolved.ApiKey),
            ApiKeyEnv = null,
            ContextWindow = resolved.ContextWindow,
            MaxTurns = resolved.MaxTurns,
            Retry = resolved.Retry,
            Proxy = new ProxySettings
            {
                Mode = mode,
                Url = cfg.Proxy.Url,
            },
            Headers = headers,
            ExtraHeadersEnabled = cfg.ExtraHeadersEnabled ?? true,
            Providers = providers,
            DataDir = _dataDir,
            ConfigPath = Path.Combine(_dataDir, "config.json"),
            Network = network,
        });
    }

    public Task<object?> SettingsSaveModelAsync(JsonElement? p, CancellationToken ct)
    {
        var provider = p?.TryGetProperty("provider", out var pv) == true ? pv.GetString() : null;
        if (string.IsNullOrWhiteSpace(provider))
            throw RpcException.ParamsInvalid("provider 不能为空");

        var notes = new List<string>();

        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal,
                "配置文件损坏，已拒绝保存以免覆盖：" + ex.Message,
                "请先修复或删除 " + Path.Combine(_dataDir, "config.json"));
        }

        cfg.Provider = provider.Trim();

        if (p?.TryGetProperty("model", out var me) == true)
            cfg.Model = NullIfBlank(me.GetString());
        if (p?.TryGetProperty("baseUrl", out var be) == true)
            cfg.BaseUrl = NullIfBlank(be.GetString());
        if (p?.TryGetProperty("contextWindow", out var cw) == true && cw.ValueKind == JsonValueKind.Number)
            cfg.ContextWindow = cw.GetInt32();
        if (p?.TryGetProperty("maxTurns", out var mt) == true && mt.ValueKind == JsonValueKind.Number)
            cfg.MaxTurns = mt.GetInt32();

        var clearKey = p?.TryGetProperty("clearKey", out var ck) == true && ck.ValueKind == JsonValueKind.True;
        var apiKey = p?.TryGetProperty("apiKey", out var ak) == true ? ak.GetString() : null;

        if (clearKey)
        {
            cfg.ApiKey = null;
            if (cfg.Providers.TryGetValue(cfg.Provider, out var pdef))
                pdef.ApiKey = null;
            notes.Add("已清除 API Key");
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (!cfg.Providers.TryGetValue(cfg.Provider, out var def))
            {
                def = new ProviderDef();
                cfg.Providers[cfg.Provider] = def;
            }
            def.ApiKey = apiKey.Trim();
            notes.Add("API Key 已保存到 provider「" + cfg.Provider + "」");
        }

        ConfigStore.Save(cfg);

        return Task.FromResult<object?>(new SettingsSaveModelResult
        {
            Ok = true,
            Notes = notes,
        });
    }

    public Task<object?> SettingsSaveNetworkAsync(JsonElement? p, CancellationToken ct)
    {
        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal,
                "配置文件损坏，已拒绝保存以免覆盖：" + ex.Message);
        }

        var notes = new List<string>();

        var mode = p?.TryGetProperty("mode", out var mv) == true ? mv.GetString() : null;
        if (mode is not null)
        {
            var normalized = mode.ToLowerInvariant() switch
            {
                "custom" => "manual",
                "system" => "auto",
                "auto" or "direct" or "manual" => mode.ToLowerInvariant(),
                _ => throw RpcException.ParamsInvalid(
                    "mode 只能是 auto/direct/manual（收到 " + mode + "）"),
            };
            cfg.Proxy.Mode = normalized;
        }

        if (p?.TryGetProperty("url", out var uv) == true)
            cfg.Proxy.Url = NullIfBlank(uv.GetString());

        if (p?.TryGetProperty("retry", out var rv) == true && rv.ValueKind == JsonValueKind.Number)
            cfg.Retry = Math.Clamp(rv.GetInt32(), 0, 8);

        if (p?.TryGetProperty("headersEnabled", out var he) == true)
            cfg.ExtraHeadersEnabled = he.ValueKind == JsonValueKind.True;

        if (p?.TryGetProperty("headers", out var hv) == true && hv.ValueKind == JsonValueKind.Array)
        {
            var (dict, skipped) = ToHeaderDict(hv);
            cfg.ExtraHeaders = dict;
            if (skipped.Count > 0)
                notes.Add("已忽略保留头：" + string.Join("、", skipped));
        }

        if (p?.TryGetProperty("providerHeaders", out var phv) == true)
        {
            if (phv.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in phv.EnumerateObject())
                {
                    if (!cfg.Providers.TryGetValue(prop.Name, out var def))
                    {
                        def = new ProviderDef();
                        cfg.Providers[prop.Name] = def;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var (dict, skipped) = ToHeaderDict(prop.Value);
                        def.ExtraHeaders = dict;
                        if (skipped.Count > 0)
                            notes.Add("服务商「" + prop.Name + "」已忽略保留头：" + string.Join("、", skipped));
                    }
                }
            }
            // 兼容旧的数组写法：忽略（无服务商归属，无法落盘），并提示
            else if (phv.ValueKind == JsonValueKind.Array)
            {
                notes.Add("providerHeaders 为数组已忽略（应为对象映射）");
            }
        }

        ConfigStore.Save(cfg);

        return Task.FromResult<object?>(new SettingsSaveNetworkResult
        {
            Ok = true,
            Notes = notes,
        });
    }

    public async Task<object?> SettingsProbeModelsAsync(JsonElement? p, CancellationToken ct)
    {
        var baseUrl = p?.TryGetProperty("baseUrl", out var bv) == true ? bv.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw RpcException.ParamsInvalid("baseUrl 必须是合法绝对地址");

        var apiKey = p?.TryGetProperty("apiKey", out var kv) == true ? kv.GetString() : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        string? proxyUrl = null;
        try { proxyUrl = ConfigStore.Resolve(ConfigStore.Load()).ProxyUrl; }
        catch { }

        var client = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = baseUrl!.Trim(),
            Model = "",
            ApiKey = apiKey,
            ProxyUrl = proxyUrl,
        });

        try
        {
            var models = await client.ListModelsAsync(cts.Token).ConfigureAwait(false);
            return new SettingsProbeModelsResult { Models = models.ToList() };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new RpcException(RpcError.LlmTimeout,
                "探测模型超时（10 秒）", "检查网络或服务地址");
        }
        catch (HttpRequestException ex)
        {
            throw new RpcException(RpcError.LlmError,
                "探测失败：" + ex.Message, "检查 BaseUrl 与 API Key");
        }
        finally
        {
            client.Dispose();
        }
    }

    public Task<object?> SettingsSaveExtraHeadersAsync(JsonElement? p, CancellationToken ct)
    {
        if (p?.TryGetProperty("enabled", out var ev) != true)
            throw RpcException.ParamsInvalid("enabled 必填");

        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal,
                "配置文件损坏，已拒绝保存以免覆盖：" + ex.Message);
        }

        cfg.ExtraHeadersEnabled = ev.ValueKind == JsonValueKind.True;
        ConfigStore.Save(cfg);

        return Task.FromResult<object?>(new SettingsSaveExtraHeadersResult
        {
            Ok = true,
            Notes = new[] { cfg.ExtraHeadersEnabled == true ? "扩展请求头已启用" : "扩展请求头已全部停用" },
        });
    }

    public Task<object?> SettingsMcpGetAsync(JsonElement? p, CancellationToken ct)
    {
        var path = Path.Combine(_dataDir, "mcp.json");
        var text = "";
        if (File.Exists(path))
        {
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                throw new RpcException(RpcError.Internal, "读取 mcp.json 失败：" + ex.Message);
            }
        }
        return Task.FromResult<object?>(new SettingsMcpGetResult { Path = path, Text = text });
    }

    public Task<object?> SettingsMcpSaveAsync(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var tv) == true ? tv.GetString() : null;
        if (text is null)
            throw RpcException.ParamsInvalid("text 不能为空");

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw RpcException.ParamsInvalid("mcp.json 顶层必须是对象");
            }
            catch (JsonException ex)
            {
                throw new RpcException(RpcError.ParamsInvalid,
                    "mcp.json 不是合法 JSON：" + ex.Message, "第 " + ex.LineNumber + " 行");
            }
        }

        var path = Path.Combine(_dataDir, "mcp.json");
        Directory.CreateDirectory(_dataDir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);

        return Task.FromResult<object?>(new SettingsMcpSaveResult
        {
            Ok = true,
            Notes = new[] { "已保存；重启应用后生效" },
        });
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static (Dictionary<string, string> dict, List<string> skipped)
        ToHeaderDict(JsonElement arr)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            // 前端 HeaderRow 用 {key,value}，契约 HeaderPair 用 {name,value}：两者都接受
            var key = el.TryGetProperty("key", out var k) ? k.GetString()?.Trim()
                : el.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            var val = el.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (val is null) continue;

            if (ConfigStore.IsReservedHeader(key))
            {
                skipped.Add(key);
                continue;
            }
            dict[key] = val;
        }
        return (dict, skipped);
    }
}
