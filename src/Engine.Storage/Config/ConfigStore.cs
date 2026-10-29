using System.Text.Json;
using HaloAgent.Engine.Storage;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>解析产物（P10 新语义：只认 active_provider/active_model）。</summary>
public sealed record ResolvedActiveModel
{
    public string ProviderId { get; init; } = "";
    public string ProviderDisplay { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string ModelDisplay { get; init; } = "";
    public string Protocol { get; init; } = "chat";
    public int? ContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    public bool AcceptsImages { get; init; }
    public bool IncludeReasoningHistory { get; init; } = true;
    public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
    /// <summary>已按 useProxy 折叠：有效代理存在且 use_proxy==true 才有值。</summary>
    public string? ProxyUrl { get; init; }
    /// <summary>全局代理是否有可用（manual 且 url 合法）。</summary>
    public bool GlobalProxyAvailable { get; init; }
    /// <summary>全局代理 url（与 useProxy 无关）。</summary>
    public string? GlobalProxyUrl { get; init; }
    /// <summary>本次模型请求是否走代理。</summary>
    public bool ProxyInUse { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public int MaxTurns { get; init; } = 200;
    public int Retry { get; init; }
    public string Persona { get; init; } = "";
    public bool Configured { get; init; }
    // ---- 兼容旧名（减少调用方改动） ----
    public string Model => ModelId;
    public string ProviderName => ProviderId;
}

/// <summary>旧 ResolvedConfig 别名（P10 已由 ResolvedActiveModel 替代，保留兼容）。</summary>

[Obsolete("Use ResolvedActiveModel")]
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

    /// <summary>新形状嗅探：含 providers.&lt;x&gt;.models 数组且有 active_provider 键即新形状。</summary>
    public static bool IsNewShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("active_provider", out _)) return false;
        if (!root.TryGetProperty("providers", out var pv) || pv.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var prop in pv.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                prop.Value.TryGetProperty("models", out var m) &&
                m.ValueKind == JsonValueKind.Array)
                return true;
        }
        return false;
    }

    public static bool IsNewShapeJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return IsNewShape(doc.RootElement);
        }
        catch { return false; }
    }

    /// <summary>读配置；文件不存在或解析失败都返回默认（不崩）。旧形状返回空新模型（未配置态）。</summary>
    public static ConfigModel Load()
    {
        var path = Paths.ConfigFile;
        if (!File.Exists(path)) return new ConfigModel();
        string text;
        try { text = File.ReadAllText(path); }
        catch { return new ConfigModel(); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { return new ConfigModel(); }   // 损坏态：返回默认（与旧形状区分，见 LoadForWrite）
        using (doc)
        {
            if (!IsNewShape(doc.RootElement))
            {
                // 旧形状：视为无效，返回空新模型；备份由调用方触发一次
                TryBackupOnce(path);
                return new ConfigModel();
            }
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
        }
        catch
        {
            return new ConfigModel();
        }
    }

    /// <summary>
    /// 读配置**用于写入**：绝不容错。
    /// 旧形状同样拒绝写入（避免整份覆写清空用户 key）。
    /// <paramref name="forceOverwriteOldShape"/> 仅由前端在 migrationRequired 两步确认后传 true：
    /// 先备份、再按空基准重建整树（用户已重填全部配置）。
    /// </summary>
    public static ConfigModel LoadForWrite(bool forceOverwriteOldShape = false)
    {
        var path = Paths.ConfigFile;
        if (!File.Exists(path)) return new ConfigModel();

        var text = File.ReadAllText(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (Exception ex)
        {
            if (!forceOverwriteOldShape)
                throw new InvalidOperationException(
                    $"配置文件解析失败，已中止保存以免覆盖原文（{path}）：{ex.Message}", ex);
            TryBackupOnce(path);
            return new ConfigModel();
        }
        using (doc)
        {
            if (!IsNewShape(doc.RootElement))
            {
                TryBackupOnce(path);
                if (!forceOverwriteOldShape)
                    throw new InvalidOperationException(
                        $"配置文件为旧结构，已自动备份（{path}.bak.*），请重建 providers 后再保存。");
                return new ConfigModel();
            }
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
        }
        catch (Exception ex)
        {
            if (!forceOverwriteOldShape)
                throw new InvalidOperationException(
                    $"配置文件解析失败，已中止保存以免覆盖原文（{path}）：{ex.Message}", ex);
            TryBackupOnce(path);
            return new ConfigModel();
        }
    }

    /// <summary>自动备份一次（幂等：同秒追加 -n；已存在 bak 则不再重复备份）。</summary>
    public static void TryBackupOnce(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path) ?? ".";
            var name = Path.GetFileName(path);
            var existing = Directory.Exists(dir)
                ? Directory.GetFiles(dir, name + ".bak.*")
                : Array.Empty<string>();
            if (existing.Length > 0) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bak = Path.Combine(dir, name + ".bak." + stamp);
            int n = 0;
            var finalBak = bak;
            while (File.Exists(finalBak))
            {
                n++;
                finalBak = bak + "-" + n;
            }
            File.Copy(path, finalBak);
        }
        catch { /* 备份失败不中断 */ }
    }

    public static void Save(ConfigModel cfg)
    {
        Directory.CreateDirectory(Paths.DataDir);
        cfg.BuildTag = ConfigTag;
        var json = JsonSerializer.Serialize(cfg, StorageJson.Options);

        // 原子写：先写临时文件再替换
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
        File.Move(tmp, Paths.ConfigFile);
    }

    /// <summary>不再与内置做字段合并：直接返回用户层（根除内置改名改地址覆盖用户手填类 bug）。</summary>
    public static Dictionary<string, ProviderConfig> AllProviders(ConfigModel cfg) => cfg.Providers;

    /// <summary>解析生效参数（P10 新语义：只认 active_provider/active_model）。</summary>
    public static ResolvedActiveModel Resolve(ConfigModel cfg)
    {
        var providerId = cfg.ActiveProvider?.Trim() ?? "";
        var modelId = cfg.ActiveModel?.Trim() ?? "";
        if (string.IsNullOrEmpty(providerId) && string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("未配置模型：请在模型设置页选择 active provider/model 后再试。");
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
            throw new InvalidOperationException("配置非法：active_provider 与 active_model 必须同时存在或同时为空。");

        if (!cfg.Providers.TryGetValue(providerId, out var pdef))
            throw new InvalidOperationException($"未找到服务商: {providerId}（请检查 providers 配置）");
        var mdef = pdef.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        if (mdef is null)
            throw new InvalidOperationException($"服务商「{providerId}」下未找到模型: {modelId}");
        if (pdef.Models.Count == 0)
            throw new InvalidOperationException($"服务商「{providerId}」下没有可用模型（active 不可指向空 provider）");

        var baseUrl = (pdef.BaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException($"服务商「{providerId}」的 base_url 为空");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"服务商「{providerId}」的 base_url 非法：{baseUrl}");

        var (globalAvailable, globalUrl) = GlobalProxy(cfg);
        var proxyUrl = (globalAvailable && pdef.UseProxy) ? globalUrl : null;

        return new ResolvedActiveModel
        {
            ProviderId = providerId,
            ProviderDisplay = string.IsNullOrWhiteSpace(pdef.DisplayName) ? providerId : pdef.DisplayName!.Trim(),
            ModelId = mdef.Id,
            ModelDisplay = string.IsNullOrWhiteSpace(mdef.DisplayName) ? mdef.Id : mdef.DisplayName!.Trim(),
            Protocol = (mdef.Protocol ?? "chat").ToLowerInvariant(),
            ContextWindow = mdef.ContextWindow,
            MaxTokens = mdef.MaxTokens,
            AcceptsImages = mdef.AcceptsImages,
            IncludeReasoningHistory = mdef.IncludeReasoningHistory,
            BaseUrl = baseUrl,
            ApiKey = string.IsNullOrEmpty(pdef.ApiKey) ? null : pdef.ApiKey,
            ProxyUrl = proxyUrl,
            GlobalProxyAvailable = globalAvailable,
            GlobalProxyUrl = globalUrl,
            ProxyInUse = proxyUrl is not null,

            Headers = ResolveHeaders(cfg, providerId),
            MaxTurns = cfg.MaxTurns ?? 200,
            Retry = Math.Clamp(cfg.Retry ?? 0, 0, 8),
            Persona = cfg.Persona ?? "",
            Configured = true,
        };
    }

    /// <summary>全局代理：manual 且 url 合法才有可用；auto/direct 均为无（auto 钉死为直连）。</summary>
    public static (bool available, string? url) GlobalProxy(ConfigModel cfg)
    {
        var mode = (cfg.Proxy.Mode ?? "auto").ToLowerInvariant();
        if (mode == "manual")
        {
            var url = cfg.Proxy.Url?.Trim();
            if (!string.IsNullOrWhiteSpace(url) && IsValidProxyUrl(url))
                return (true, url);
            return (false, null);
        }
        // auto/direct/off/none：无可用（auto 保持现状行为并在注释写死，本期不偷换为跟随系统）
        return (false, null);
    }

    /// <summary>全局代理 url（供 WebFetch/下载等非模型链路使用，与 provider.useProxy 无关）。</summary>
    public static string? GlobalProxyUrl(ConfigModel cfg)
    {
        var (avail, url) = GlobalProxy(cfg);
        return avail ? url : null;
    }

    public static bool IsValidProxyUrl(string url)
    {
        var u = url.Trim();
        return (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && u.Length > 8;
    }

    /// <summary>解析生效请求头（仅 provider 层）。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ResolveHeaders(
        ConfigModel cfg, string providerId)
    {
        if (!cfg.Providers.TryGetValue(providerId, out var def))
            return Array.Empty<KeyValuePair<string, string>>();
        if (!def.HeadersEnabled)
            return Array.Empty<KeyValuePair<string, string>>();

        var result = new List<KeyValuePair<string, string>>();
        ApplyHeaderLayer(def.Headers ?? new Dictionary<string, string>(), result);
        return result;
    }

    private static void ApplyHeaderLayer(
        Dictionary<string, string> layer, List<KeyValuePair<string, string>> result)
    {
        // 排序让结果稳定
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
