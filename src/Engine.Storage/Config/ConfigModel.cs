using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>
/// config.json 的模型（P10 新形状：Provider 1—N Model 全量树）。
///
/// 破坏性：不再保留顶层 model/base_url/api_key/provider/context_window，
/// 也不做旧文件自动迁移（见 ConfigStore嗅探/备份）。
/// </summary>
public sealed class ConfigModel
{
    [JsonPropertyName("active_provider")] public string? ActiveProvider { get; set; }
    [JsonPropertyName("active_model")] public string? ActiveModel { get; set; }

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("max_turns")] public int? MaxTurns { get; set; }
    [JsonPropertyName("persona")] public string? Persona { get; set; }

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
    [JsonPropertyName("use_proxy")] public bool UseProxy { get; set; } = true;
    [JsonPropertyName("models")]
    public List<ModelConfig> Models { get; set; } = new();
}

public sealed class ModelConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    /// <summary>chat 可用；responses 保留枚举但前端 disabled、引擎拒绝执行。</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "chat";
    [JsonPropertyName("context_window")] public int? ContextWindow { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("accepts_images")] public bool AcceptsImages { get; set; }
    [JsonPropertyName("include_reasoning_history")] public bool IncludeReasoningHistory { get; set; } = true;
}

public sealed class ProxyConfig
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }   // auto / direct / manual
    [JsonPropertyName("url")] public string? Url { get; set; }
}
