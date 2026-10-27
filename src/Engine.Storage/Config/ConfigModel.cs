using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>
/// config.json 的模型。
///
/// ============ 命名策略 ============
/// 存储用 snake_case（与既有格式一致，文件是给人看的）。
/// 通信用的 camelCase 在 P1 的 HaloJson.Options 里，两套必须分开。
/// </summary>
public sealed class ConfigModel
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderDef> Providers { get; set; } = new();

    [JsonPropertyName("context_window")] public int? ContextWindow { get; set; }
    [JsonPropertyName("max_turns")] public int? MaxTurns { get; set; }
    [JsonPropertyName("persona")] public string? Persona { get; set; }

    [JsonPropertyName("build_tag")] public string? BuildTag { get; set; }

    [JsonPropertyName("proxy")]
    public ProxyConfig Proxy { get; set; } = new();

    [JsonPropertyName("retry")] public int? Retry { get; set; }

    [JsonPropertyName("extra_headers_enabled")] public bool? ExtraHeadersEnabled { get; set; }

    [JsonPropertyName("extra_headers")]
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
}

public sealed class ProviderDef
{
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("api_key_env")] public string? ApiKeyEnv { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("context_window")] public int? ContextWindow { get; set; }
    [JsonPropertyName("extra_headers_enabled")] public bool? ExtraHeadersEnabled { get; set; }
    /// <summary>请求头模板：值可含 `{{session_id}}` / `{{session_hash}}`，请求时按会话展开。</summary>
    [JsonPropertyName("extra_headers")]
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
}

public sealed class ProxyConfig
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }   // auto / direct / manual
    [JsonPropertyName("url")] public string? Url { get; set; }
}
