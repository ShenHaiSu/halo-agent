using System.Text.Json;
using HaloAgent.Engine.Storage;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>解析好的生效参数（随全链路走）。</summary>
public sealed record ResolvedConfig
{
    public string Model { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
    public string ProviderName { get; init; } = "";
    public int? ContextWindow { get; init; }
    public string? ProxyUrl { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public int MaxTurns { get; init; } = 200;
    public int Retry { get; init; }
    public string Persona { get; init; } = "";
    public bool Configured { get; init; }
}

public static class ConfigStore
{
    private const string ConfigTag = "h1a2l3o4a5g";

    /// <summary>读配置；文件不存在或解析失败都返回默认（不崩）。</summary>
    public static ConfigModel Load()
    {
        var path = Paths.ConfigFile;
        if (!File.Exists(path)) return new ConfigModel();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
        }
        catch
        {
            return new ConfigModel();
        }
    }

    /// <summary>
    /// 读配置**用于写入**：绝不容错。
    ///
    /// 落盘是整份写回，所以拿默认值顶上等于把用户原有的 provider 条目、
    /// key、人格、轮数一起清空 —— 不可逆。
    /// 宁可拒绝保存，让他先去修文件。
    /// </summary>
    public static ConfigModel LoadForWrite()
    {
        var path = Paths.ConfigFile;
        if (!File.Exists(path)) return new ConfigModel();

        var text = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"配置文件解析失败，已中止保存以免覆盖原文（{path}）：{ex.Message}", ex);
        }
    }

    public static void Save(ConfigModel cfg)
    {
        Directory.CreateDirectory(Paths.DataDir);
        cfg.BuildTag = ConfigTag;
        var json = JsonSerializer.Serialize(cfg, StorageJson.Options);

        // 原子写：先写临时文件再替换，防"写到一半断电"导致配置损坏
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
        File.Move(tmp, Paths.ConfigFile);
    }

    /// <summary>合并内置 + 用户 provider（**字段级**合并，不是整条替换）。</summary>
    public static Dictionary<string, ProviderDef> AllProviders(ConfigModel cfg)
    {
        var merged = BuiltinProviders.All();
        foreach (var (name, user) in cfg.Providers)
        {
            if (!merged.TryGetValue(name, out var baseDef))
            {
                merged[name] = user;
                continue;
            }
            // 只覆盖用户写了值的字段
            if (user.BaseUrl is not null) baseDef.BaseUrl = user.BaseUrl;
            if (user.Model is not null) baseDef.Model = user.Model;
            if (user.ApiKey is not null) baseDef.ApiKey = user.ApiKey;
            if (user.ApiKeyEnv is not null) baseDef.ApiKeyEnv = user.ApiKeyEnv;
            if (user.ContextWindow is not null) baseDef.ContextWindow = user.ContextWindow;
            if (user.ExtraHeadersEnabled is not null) baseDef.ExtraHeadersEnabled = user.ExtraHeadersEnabled;
            if (user.ExtraHeaders.Count > 0) baseDef.ExtraHeaders = user.ExtraHeaders;
        }
        return merged;
    }

    /// <summary>
    /// 解析生效参数。优先级：CLI > 环境变量 > 顶层 config > provider 预设。
    /// </summary>
    public static ResolvedConfig Resolve(
        ConfigModel cfg,
        string? cliModel = null,
        string? cliBaseUrl = null,
        string? cliApiKey = null,
        string? cliProvider = null,
        string? cliProxy = null,
        bool noProxy = false)
    {
        string? Env(params string[] names) => names
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var providerName = cliProvider
            ?? Env("HALO_PROVIDER")
            ?? cfg.Provider
            ?? "ollama";

        var providers = AllProviders(cfg);
        var pdef = providers.GetValueOrDefault(providerName) ?? new ProviderDef
        {
            BaseUrl = $"https://{providerName}/v1",
        };

        // base_url：CLI > env > 顶层 config > provider 预设
        var baseUrl = cliBaseUrl
            ?? Env("HALO_BASE_URL", "OPENAI_BASE_URL")
            ?? cfg.BaseUrl
            ?? pdef.BaseUrl
            ?? "http://localhost:11434/v1";

        // model：CLI > env > 顶层 config > 预设；都没有则报错
        var model = cliModel
            ?? Env("HALO_MODEL", "OPENAI_MODEL")
            ?? cfg.Model
            ?? pdef.Model
            ?? throw new InvalidOperationException(
                $"未配置模型：请用 --model 指定，或设置 HALO_MODEL / config.json 的 model / " +
                $"provider「{providerName}」的 model");

        // key：CLI > env > 顶层 config > provider 明文 > provider 环境变量名
        var apiKey = cliApiKey
            ?? Env("HALO_API_KEY", "OPENAI_API_KEY")
            ?? (string.IsNullOrEmpty(cfg.ApiKey) ? null : cfg.ApiKey)
            ?? (string.IsNullOrEmpty(pdef.ApiKey) ? null : pdef.ApiKey)
            ?? (pdef.ApiKeyEnv is null ? null : Env(pdef.ApiKeyEnv));

        // 代理：CLI > 专用 env > 配置文件
        string? proxyUrl;
        if (noProxy) proxyUrl = null;
        else if (!string.IsNullOrWhiteSpace(cliProxy)) proxyUrl = cliProxy.Trim();
        else if (!string.IsNullOrWhiteSpace(Env("HALO_PROXY"))) proxyUrl = Env("HALO_PROXY")!.Trim();
        else proxyUrl = ResolveProxy(cfg);

        return new ResolvedConfig
        {
            Model = model,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ProviderName = providerName,
            ContextWindow = pdef.ContextWindow ?? cfg.ContextWindow
                            ?? ContextWindows.ForModel(model),
            ProxyUrl = proxyUrl,
            Headers = ResolveHeaders(cfg, providerName),
            MaxTurns = cfg.MaxTurns ?? 200,
            Retry = Math.Clamp(cfg.Retry ?? 0, 0, 8),
            Persona = cfg.Persona ?? "",
            Configured = !string.IsNullOrWhiteSpace(apiKey) || providerName == "ollama",
        };
    }

    private static string? ResolveProxy(ConfigModel cfg)
    {
        var mode = (cfg.Proxy.Mode ?? "auto").ToLowerInvariant();
        if (mode is "direct" or "off" or "none") return null;
        if (mode == "manual")
        {
            var url = cfg.Proxy.Url;
            if (!string.IsNullOrWhiteSpace(url) && IsValidProxyUrl(url)) return url.Trim();
            return null;   // 非法地址 → 回落 auto
        }
        // auto：交给 HttpClient 读标准环境变量
        return null;
    }

    private static bool IsValidProxyUrl(string url)
    {
        var u = url.Trim();
        return (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && u.Length > 8;
    }

    /// <summary>解析生效请求头（顶层 ⊕ provider 层叠加）。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ResolveHeaders(
        ConfigModel cfg, string providerName)
    {
        // 总闸关了 → 一条都不发
        if (cfg.ExtraHeadersEnabled == false)
            return Array.Empty<KeyValuePair<string, string>>();

        var result = new List<KeyValuePair<string, string>>();
        ApplyHeaderLayer(cfg.ExtraHeaders, result);

        var providerDef = AllProviders(cfg).GetValueOrDefault(providerName);
        if (providerDef is not null && providerDef.ExtraHeadersEnabled != false)
            ApplyHeaderLayer(providerDef.ExtraHeaders, result);

        return result;
    }

    private static void ApplyHeaderLayer(
        Dictionary<string, string> layer, List<KeyValuePair<string, string>> result)
    {
        // 排序让结果稳定（HashMap 遍历顺序不定）
        foreach (var key in layer.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var raw = layer[key];

            // 空值 = 删掉继承来的那条
            if (string.IsNullOrWhiteSpace(raw))
            {
                result.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (IsReservedHeader(key)) continue;

            var (expanded, _) = ExpandEnvVars(raw);
            result.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            result.Add(new KeyValuePair<string, string>(key, expanded));
        }
    }

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "host", "content-type", "content-length",
        "content-encoding", "transfer-encoding", "connection",
    };

    public static bool IsReservedHeader(string name) => ReservedHeaders.Contains(name);

    /// <summary>展开 ${VAR} / $VAR；未设置的环境变量原样保留。</summary>
    private static (string value, List<string> missing) ExpandEnvVars(string value)
    {
        var missing = new List<string>();
        if (!value.Contains('$')) return (value, missing);

        var sb = new System.Text.StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] != '$')
            {
                sb.Append(value[i]);
                i++;
                continue;
            }

            // ${VAR}
            if (i + 1 < value.Length && value[i + 1] == '{')
            {
                var close = value.IndexOf('}', i + 2);
                if (close < 0) { sb.Append(value[i..]); break; }
                var name = value[(i + 2)..close];
                var v = Environment.GetEnvironmentVariable(name);
                if (v is null)
                {
                    sb.Append(value[i..(close + 1)]);
                    missing.Add(name);
                }
                else sb.Append(v);
                i = close + 1;
                continue;
            }

            // $VAR
            int j = i + 1;
            while (j < value.Length &&
                   ((j == i + 1 && (char.IsLetter(value[j]) || value[j] == '_')) ||
                    (j > i + 1 && (char.IsLetterOrDigit(value[j]) || value[j] == '_'))))
                j++;

            if (j == i + 1)
            {
                sb.Append('$');
                i++;
                continue;
            }

            var varName = value[(i + 1)..j];
            var val = Environment.GetEnvironmentVariable(varName);
            if (val is null)
            {
                sb.Append(value[i..j]);
                missing.Add(varName);
            }
            else sb.Append(val);
            i = j;
        }
        return (sb.ToString(), missing);
    }

    /// <summary>会话模板定义：同会话稳定、`session_hash = SHA256(sessionId)[:16，小写hex]`。</summary>
    public static class HeaderTemplate
    {
        public const string SessionIdToken = "{{session_id}}";
        public const string SessionHashToken = "{{session_hash}}";

        public static bool ContainsSessionTemplate(string v) =>
            v.Contains(SessionIdToken, StringComparison.OrdinalIgnoreCase) ||
            v.Contains(SessionHashToken, StringComparison.OrdinalIgnoreCase);

        public static string SessionHash(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "";
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }

        /// <summary>展开一批模板头；无会话时含模板的头整条跳过（Validate/ListModels 用）。</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ExpandAll(
            IReadOnlyList<KeyValuePair<string, string>> templates, string? sessionId)
        {
            if (templates.Count == 0) return templates;
            var sid = sessionId ?? "";
            var hash = SessionHash(sessionId);
            var out_ = new List<KeyValuePair<string, string>>(templates.Count);
            foreach (var (k, v) in templates)
            {
                if (ContainsSessionTemplate(v) && string.IsNullOrEmpty(sid)) continue;
                var e = v.Replace(SessionIdToken, sid, StringComparison.OrdinalIgnoreCase)
                         .Replace(SessionHashToken, hash, StringComparison.OrdinalIgnoreCase);
                out_.Add(new(k, e));
            }
            return out_;
        }
    }
}
