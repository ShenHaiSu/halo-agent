using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>
/// config.json 的模型（新形状：Provider 1—N Model 全量树）。
/// Provider 网络选项 network 取代 use_proxy；proxy.mode 三档 system/direct/manual。
///
/// 破坏性：不再保留顶层 model/base_url/api_key/provider/context_window，
/// 也不再做旧文件自动迁移（见 ConfigStore嗅探/备份）。
/// </summary>
public sealed class ConfigModel
{
    [JsonPropertyName("active_provider")] public string? ActiveProvider { get; set; }
    [JsonPropertyName("active_model")] public string? ActiveModel { get; set; }

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new(StringComparer.Ordinal);

     [JsonPropertyName("max_turns")] public int? MaxTurns { get; set; }
     [JsonPropertyName("persona")] public string? Persona { get; set; }
     [JsonPropertyName("compact_keep_tail")] public int? CompactKeepTail { get; set; }
     [JsonPropertyName("auto_compact_on_limit")] public bool? AutoCompactOnLimit { get; set; }
 
     [JsonPropertyName("build_tag")] public string? BuildTag { get; set; }
 
     [JsonPropertyName("proxy")]
     public ProxyConfig Proxy { get; set; } = new();
 
     [JsonPropertyName("retry")] public int? Retry { get; set; }
}

 public sealed class ProviderConfig
 {
     [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
     [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
     [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
     [JsonPropertyName("headers_enabled")] public bool HeadersEnabled { get; set; } = true;
     [JsonPropertyName("headers")]
     public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
     /// <summary>follow | proxy | direct（取代 use_proxy）。</summary>
     [JsonPropertyName("network")] public string Network { get; set; } = "follow";
     /// <summary>
     /// responses 协议的 store 三档（P26）：
     ///   "default"（缺省）= 请求体不含 store 键，由端点自行决定（最大兼容）；
     ///   "true"           = 显式 store=true（服务端持久化，previous_response_id 可增量）；
     ///   "false"          = 显式 store=false（P13 定案口径）。
     /// 仅对 protocol=responses 的模型生效；chat 模型忽略且不写入请求体。
     /// 归属 Provider 而非 Model：持久化是端点能力，同端点下所有 responses 模型必然一致。
     /// </summary>
     [JsonPropertyName("response_store")]
     public string ResponseStore { get; set; } = HaloAgent.Engine.Abstractions.Types.ResponseStores.Default;
     /// <summary>预留扩展位：Provider 独立代理地址（本次只留位，不实现）。</summary>
     [JsonPropertyName("proxy_override")]
     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
     public string? ProxyOverride { get; set; }
     /// <summary>旧 use_proxy 读兼容（一个版本）：network 缺失时回落判定；写时永不输出。</summary>
     [JsonPropertyName("use_proxy")]
     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
     public bool? UseProxyLegacy { get; set; }
     [JsonPropertyName("models")]
     public List<ModelConfig> Models { get; set; } = new();
 }

public sealed class ModelConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    /// <summary>chat / responses 均可用。</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "chat";
    [JsonPropertyName("context_window")] public int? ContextWindow { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("accepts_images")] public bool AcceptsImages { get; set; }
    [JsonPropertyName("include_reasoning_history")] public bool IncludeReasoningHistory { get; set; } = true;
}

public sealed class ProxyConfig
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}
