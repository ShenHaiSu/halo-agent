 using System.Text.Json;
 using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Storage;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>解析产物（新语义：只认 active_provider/active_model）。</summary>
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
     /// <summary>responses 的 store 三档（P26，来自 Provider 级配置）。</summary>
     public HaloAgent.Engine.Abstractions.Types.StoreMode StoreMode { get; init; } = HaloAgent.Engine.Abstractions.Types.StoreMode.Default;
     public string BaseUrl { get; init; } = "";
    public string? ApiKey { get; init; }
     /// <summary>兼容保留：仅 manual 有效时有值；诊断一律读 ProxyKind。</summary>
     public string? ProxyUrl { get; init; }
     /// <summary>最终生效代理值对象。</summary>
     public EffectiveProxy EffectiveProxy { get; init; } = EffectiveProxy.None;
     /// <summary>None | Manual | System。</summary>
     public ProxyKind ProxyKind { get; init; } = ProxyKind.None;
     /// <summary>global-direct|global-manual|global-system|provider-direct|provider-proxy|bypass-local。</summary>
     public string ProxySource { get; init; } = ProxySources.GlobalDirect;
     /// <summary>代理组合降级 warning（空串=无）。</summary>
     public string ProxyWarning { get; init; } = "";
     /// <summary>全局代理是否有可用（manual 合法或 system；direct/非法 manual 为无）。</summary>
     public bool GlobalProxyAvailable { get; init; }
     /// <summary>全局代理 url（仅 manual 有效；system/direct 下为 null）。</summary>
     public string? GlobalProxyUrl { get; init; }
     /// <summary>本次模型请求是否走代理（system 下保守标 true）。</summary>
     public bool ProxyInUse { get; init; }
     public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
         = Array.Empty<KeyValuePair<string, string>>();
     public int MaxTurns { get; init; } = 200;
     public int Retry { get; init; }
     public string Persona { get; init; } = "";
     public int CompactKeepTail { get; init; } = 2;
     public bool AutoCompactOnLimit { get; init; }
     public bool Configured { get; init; }
    // ---- 兼容旧名（减少调用方改动） ----
    public string Model => ModelId;
    public string ProviderName => ProviderId;
}

/// <summary>旧 ResolvedConfig 别名（已由 ResolvedActiveModel 替代，保留兼容）。</summary>

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

     /// <summary>读配置；文件不存在或解析失败都返回默认（不崩）。旧形状返回空新模型（未配置态）。读时内存归一（不强制回写）。</summary>
     public static ConfigModel Load()
     {
         var path = Paths.ConfigFile;
         if (!File.Exists(path))
         {
             var fresh = new ConfigModel();
             NormalizeConfig(fresh, isNewFile: true);
             return fresh;
         }
         string text;
         try { text = File.ReadAllText(path); }
         catch { var d = new ConfigModel(); NormalizeConfig(d, isNewFile: false); return d; }
         JsonDocument doc;
         try { doc = JsonDocument.Parse(text); }
         catch { var d = new ConfigModel(); NormalizeConfig(d, isNewFile: false); return d; }   // 损坏态：返回默认（与旧形状区分，见 LoadForWrite）
         using (doc)
         {
             if (!IsNewShape(doc.RootElement))
             {
                 // 旧形状：视为无效，返回空新模型；备份由调用方触发一次
                 TryBackupOnce(path);
                 var d = new ConfigModel();
                 NormalizeConfig(d, isNewFile: false);
                 return d;
             }
         }
         try
         {
             var cfg = JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
             NormalizeConfig(cfg, isNewFile: false);
             return cfg;
         }
         catch
         {
             var d = new ConfigModel();
             NormalizeConfig(d, isNewFile: false);
             return d;
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
         if (!File.Exists(path)) { var fresh = new ConfigModel(); NormalizeConfig(fresh, isNewFile: true); return fresh; }
         var text = File.ReadAllText(path);
         JsonDocument doc;
         try { doc = JsonDocument.Parse(text); }
         catch (Exception ex)
         {
             if (!forceOverwriteOldShape)
                 throw new InvalidOperationException(
                     $"配置文件解析失败，已中止保存以免覆盖原文（{path}）：{ex.Message}", ex);
             TryBackupOnce(path);
             var d = new ConfigModel(); NormalizeConfig(d, isNewFile: false); return d;
         }
         using (doc)
         {
             if (!IsNewShape(doc.RootElement))
             {
                 TryBackupOnce(path);
                 if (!forceOverwriteOldShape)
                     throw new InvalidOperationException(
                         $"配置文件为旧结构，已自动备份（{path}.bak.*），请重建 providers 后再保存。");
                 var d = new ConfigModel(); NormalizeConfig(d, isNewFile: false); return d;
             }
         }
         try
         {
             var cfg = JsonSerializer.Deserialize<ConfigModel>(text, StorageJson.Options) ?? new ConfigModel();
             NormalizeConfig(cfg, isNewFile: false);
             return cfg;
         }
         catch (Exception ex)
         {
             if (!forceOverwriteOldShape)
                 throw new InvalidOperationException(
                     $"配置文件解析失败，已中止保存以免覆盖原文（{path}）：{ex.Message}", ex);
             TryBackupOnce(path);
             var d = new ConfigModel(); NormalizeConfig(d, isNewFile: false); return d;
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
         // 写时只出 network（清掉旧 use_proxy 残留，避免双源）
         foreach (var p in cfg.Providers.Values) p.UseProxyLegacy = null;
         var json = JsonSerializer.Serialize(cfg, StorageJson.Options);
         // 原子写：先写临时文件再替换
         var tmp = Paths.ConfigFile + ".tmp";
         File.WriteAllText(tmp, json);
         if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
         File.Move(tmp, Paths.ConfigFile);
     }

    /// <summary>不再与内置做字段合并：直接返回用户层（根除内置改名改地址覆盖用户手填类 bug）。</summary>
    public static Dictionary<string, ProviderConfig> AllProviders(ConfigModel cfg) => cfg.Providers;

     public const string ProxyDowngradeWarning = "该服务要求走代理，但全局无可用代理，已降级直连";
     public const string ProxyInvalidUrlWarning = "全局代理地址非法，已降级直连";
 
     /// <summary>读时内存归一（不强制回写）。全新安装缺省 system；存量守恒 direct。</summary>
     public static void NormalizeConfig(ConfigModel cfg, bool isNewFile)
     {
         cfg.Proxy ??= new ProxyConfig();
         var rawMode = (cfg.Proxy.Mode ?? "").Trim().ToLowerInvariant();
         if (string.IsNullOrEmpty(rawMode))
         {
             cfg.Proxy.Mode = isNewFile ? "system" : "direct";
         }
         else switch (rawMode)
         {
             case "system": cfg.Proxy.Mode = "system"; break;
             case "direct": cfg.Proxy.Mode = "direct"; break;
             case "manual": cfg.Proxy.Mode = "manual"; break;
             case "custom": cfg.Proxy.Mode = "manual"; break;
             case "auto": cfg.Proxy.Mode = "direct"; break; // 旧 auto 实际=直连，守恒
             default: cfg.Proxy.Mode = "direct"; break; // 未知枚举回落 direct（warning 由 Warnings() 出）
         }
         foreach (var p in cfg.Providers.Values)
         {
             var raw = (p.Network ?? "").Trim().ToLowerInvariant();
             if (string.IsNullOrEmpty(raw))
             {
                 // 读时兼容旧 use_proxy 一个版本：false→direct，否则 follow
                 p.Network = p.UseProxyLegacy == false ? "direct" : "follow";
             }
             else switch (raw)
             {
                 case "follow":
                 case "proxy":
                 case "direct": p.Network = raw; break;
                 default:
                     // 兼容旧布尔串
                     if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) p.Network = "follow";
                     else if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) p.Network = "direct";
                     else p.Network = "follow"; // 未知回落 follow（warning 由 Warnings() 出）
                     break;
             }
         }
     }
 
     /// <summary>全局 mode 归一（读侧旧映射：custom→manual；auto→direct；未知→direct）。</summary>
     public static string NormalizeGlobalMode(string? mode)
     {
         var m = (mode ?? "").Trim().ToLowerInvariant();
         return m switch
         {
             "system" => "system",
             "direct" => "direct",
             "manual" => "manual",
             "custom" => "manual",
             "auto" => "direct",
             _ => "direct",
         };
     }
 
     /// <summary>provider network 归一（未知→follow）。</summary>
     public static string NormalizeProviderNetwork(string? network, bool? useProxyLegacy = null)
     {
         var n = (network ?? "").Trim().ToLowerInvariant();
         if (string.IsNullOrEmpty(n))
             return useProxyLegacy == false ? "direct" : "follow";
         return n switch
         {
             "follow" => "follow",
             "proxy" => "proxy",
             "direct" => "direct",
             "true" => "follow",
             "false" => "direct",
             _ => "follow",
         };
     }
 
     /// <summary>唯一解析实现：Provider 强制项先裁决；永不因组合抛，只回 warning。</summary>
     public static (EffectiveProxy proxy, string warning) TryResolveEffectiveProxy(ProxyConfig global, string providerNetwork)
     {
         var network = NormalizeProviderNetwork(providerNetwork);
         if (network == "direct") return (EffectiveProxy.None, "");
         var g = NormalizeGlobalMode(global.Mode);
         EffectiveProxy eff;
         string w = "";
         switch (g)
         {
             case "direct": eff = EffectiveProxy.None; break;
             case "manual":
                 if (!IsValidProxyUrl(global.Url ?? ""))
                     return (EffectiveProxy.None, ProxyInvalidUrlWarning);
                 eff = EffectiveProxy.Manual(global.Url!.Trim()); break;
             default: eff = EffectiveProxy.System; break; // 仅显式 system 进此分支
         }
         if (network == "proxy" && eff.Kind == ProxyKind.None)
             return (EffectiveProxy.None, ProxyDowngradeWarning);
         return (eff, w);
     }
 
     /// <summary>全局档位解析（供 WebFetch 等非模型链路使用）。</summary>
     public static (EffectiveProxy proxy, string warning) ResolveGlobalEffective(ProxyConfig global)
     {
         var g = NormalizeGlobalMode(global.Mode);
         return g switch
         {
             "manual" => IsValidProxyUrl(global.Url ?? "")
                 ? (EffectiveProxy.Manual(global.Url!.Trim()), "")
                 : (EffectiveProxy.None, ProxyInvalidUrlWarning),
             "system" => (EffectiveProxy.System, ""),
             _ => (EffectiveProxy.None, ""),
         };
     }
 
     /// <summary>解析生效参数（新语义：只认 active_provider/active_model）。代理组合永不抛。</summary>
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
         var network = NormalizeProviderNetwork(pdef.Network, pdef.UseProxyLegacy);
         var (eff, warning) = TryResolveEffectiveProxy(cfg.Proxy ?? new ProxyConfig(), network);
         var (gAvail, gUrl) = GlobalProxy(cfg);
         // 本地/内网旁路：永远直连（与上表正交）
         string source;
         if (IsBypassedHost(uri.Host))
         {
             eff = EffectiveProxy.None;
             source = ProxySources.BypassLocal;
         }
         else if (network == "direct") source = ProxySources.ProviderDirect;
         else if (network == "proxy") source = eff.Kind == ProxyKind.None ? ProxySources.ProviderProxy : ProxySources.ProviderProxy;
         else source = NormalizeGlobalMode(cfg.Proxy?.Mode) switch
         {
             "manual" => ProxySources.GlobalManual,
             "system" => ProxySources.GlobalSystem,
             _ => ProxySources.GlobalDirect,
         };
         var proxyUrl = eff.Kind == ProxyKind.Manual ? eff.Url : null;
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
             // P26：store 来自 Provider 级配置（不是模型级）。非法值回落 Default。
             StoreMode = HaloAgent.Engine.Abstractions.Types.ResponseStores.ToStoreMode(pdef.ResponseStore),
             BaseUrl = baseUrl,
             ApiKey = string.IsNullOrEmpty(pdef.ApiKey) ? null : pdef.ApiKey,
             ProxyUrl = proxyUrl,
             EffectiveProxy = eff,
             ProxyKind = eff.Kind,
             ProxySource = source,
             ProxyWarning = warning,
             GlobalProxyAvailable = gAvail,
             GlobalProxyUrl = gUrl,
             ProxyInUse = eff.InUse,
             Headers = ResolveHeaders(cfg, providerId),
             MaxTurns = Math.Clamp(cfg.MaxTurns ?? 200, 0, 500),
             Retry = Math.Clamp(cfg.Retry ?? 0, 0, 8),
             Persona = cfg.Persona ?? "",
             CompactKeepTail = Math.Clamp(cfg.CompactKeepTail ?? 2, 1, 10),
             AutoCompactOnLimit = cfg.AutoCompactOnLimit ?? false,
             Configured = true,
         };
     }
 
     /// <summary>全局代理：manual 合法→可用；system→可用（运行时跟系统）；direct/非法→无。</summary>
     public static (bool available, string? url) GlobalProxy(ConfigModel cfg)
     {
         var mode = NormalizeGlobalMode(cfg.Proxy?.Mode);
         if (mode == "manual")
         {
             var url = cfg.Proxy?.Url?.Trim();
             if (!string.IsNullOrWhiteSpace(url) && IsValidProxyUrl(url))
                 return (true, url);
             return (false, null);
         }
         if (mode == "system") return (true, null);
         return (false, null);
     }
 
     /// <summary>全局代理 url（仅 manual 有效；system/direct 下为 null。与 provider.network 无关）。</summary>
     public static string? GlobalProxyUrl(ConfigModel cfg)
     {
         var (avail, url) = GlobalProxy(cfg);
         return avail ? url : null;
     }
 
     /// <summary>本地/内网旁路主机判定（大小写不敏感，主机小写去尾点；端口已剥离时直接传 host）。</summary>
     public static bool IsBypassedHost(string? host)
     {
         if (string.IsNullOrWhiteSpace(host)) return false;
         var h = host.Trim().ToLowerInvariant();
         if (h.StartsWith("[") && h.EndsWith("]")) h = h[1..^1]; // IPv6 方括号
         h = h.TrimEnd('.');
         // 剥端口（仅当单冒号且非 IPv6）
         int colon = h.LastIndexOf(':');
         if (colon >= 0 && h.IndexOf(':') == colon && !h.EndsWith("]"))
         {
             var maybePort = h[(colon + 1)..];
             if (maybePort.Length > 0 && maybePort.All(char.IsDigit))
                 h = h[..colon].TrimEnd('.');
         }
         if (h == "localhost" || h == "::1") return true;
         if (h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
         if (h.EndsWith(".local", StringComparison.Ordinal)) return true;
         // IPv4 网段
         var dots = h.Count(c => c == '.');
         if (dots == 3)
         {
             var parts = h.Split('.');
             if (parts.Length == 4 && parts.All(p => byte.TryParse(p, out _)))
             {
                 int a = int.Parse(parts[0]), b = int.Parse(parts[1]);
                 if (a == 127) return true; // 127/8
                 if (a == 10) return true; // 10/8
                 if (a == 172 && b >= 16 && b <= 31) return true; // 172.16/12
                 if (a == 192 && b == 168) return true; // 192.168/16
             }
         }
         return false;
     }
 
     /// <summary>URL 级旁路判定（含 host 提取失败→不旁路）。</summary>
     public static bool IsBypassedUrl(string? url)
     {
         if (string.IsNullOrWhiteSpace(url)) return false;
         try
         {
             var u = new Uri(url.Trim(), UriKind.Absolute);
             return IsBypassedHost(u.Host);
         }
         catch { return false; }
     }

     /// <summary>代理地址合法性：http(s):// 开头。</summary>
     public static bool IsValidProxyUrl(string url)
     {
         if (string.IsNullOrWhiteSpace(url)) return false;
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
