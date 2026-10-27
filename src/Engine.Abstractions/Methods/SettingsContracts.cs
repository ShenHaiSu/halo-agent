using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ settings.get ============================

/// <summary>settings.get 结果。</summary>
public sealed record SettingsGetResult
{
    /// <summary>模型名。</summary>
    public string Model { get; init; } = "";
    /// <summary>服务商名。</summary>
    public string Provider { get; init; } = "";
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>是否已配置 API Key。</summary>
    public bool ApiKeySet { get; init; }
    /// <summary>API Key 环境变量名（可为空）。</summary>
    public string? ApiKeyEnv { get; init; }
    /// <summary>上下文窗口（可为空）。</summary>
    public int? ContextWindow { get; init; }
    /// <summary>最大轮数。</summary>
    public int MaxTurns { get; init; }
    /// <summary>重试次数。</summary>
    public int Retry { get; init; }
    /// <summary>代理设置。</summary>
    public ProxySettings? Proxy { get; init; }
    /// <summary>请求头。</summary>
    public IReadOnlyList<HeaderPair> Headers { get; init; } = Array.Empty<HeaderPair>();
    /// <summary>是否启用扩展头。</summary>
    public bool ExtraHeadersEnabled { get; init; }
    /// <summary>服务商列表。</summary>
    public IReadOnlyList<ProviderEntry> Providers { get; init; } = Array.Empty<ProviderEntry>();
    /// <summary>数据目录。</summary>
    public string DataDir { get; init; } = "";
    /// <summary>配置文件路径。</summary>
    public string ConfigPath { get; init; } = "";
}

/// <summary>代理设置。</summary>
public sealed record ProxySettings
{
    /// <summary>“auto” | “direct” | “manual”。</summary>
    public string Mode { get; init; } = "auto";
    /// <summary>代理地址（可为空）。</summary>
    public string? Url { get; init; }
}

/// <summary>服务商条目。</summary>
public sealed record ProviderEntry
{
    /// <summary>服务商名。</summary>
    public string Name { get; init; } = "";
    /// <summary>模型名。</summary>
    public string Model { get; init; } = "";
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
}

// ============================ settings.saveModel ============================

/// <summary>settings.saveModel 参数。</summary>
public sealed record SettingsSaveModelParams
{
    /// <summary>服务商名。</summary>
    public string Provider { get; init; } = "";
    /// <summary>模型名（可为空）。</summary>
    public string? Model { get; init; }
    /// <summary>服务地址（可为空）。</summary>
    public string? BaseUrl { get; init; }
    /// <summary>API Key（可为空）。</summary>
    public string? ApiKey { get; init; }
    /// <summary>是否清除 Key。</summary>
    public bool ClearKey { get; init; }
    /// <summary>上下文窗口（可为空）。</summary>
    public int? ContextWindow { get; init; }
    /// <summary>最大轮数（可为空）。</summary>
    public int? MaxTurns { get; init; }
}

/// <summary>settings.saveModel 结果。</summary>
public sealed record SettingsSaveModelResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>备注。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ============================ settings.saveNetwork ============================

/// <summary>settings.saveNetwork 参数。</summary>
public sealed record SettingsSaveNetworkParams
{
    /// <summary>代理模式：“auto” | “direct” | “manual”（可为空）。</summary>
    public string? Mode { get; init; }
    /// <summary>代理地址（可为空）。</summary>
    public string? Url { get; init; }
    /// <summary>重试次数（可为空）。</summary>
    public int? Retry { get; init; }
    /// <summary>是否启用请求头（可为空）。</summary>
    public bool? HeadersEnabled { get; init; }
    /// <summary>请求头（可为空）。</summary>
    public IReadOnlyList<HeaderPair>? Headers { get; init; }
    /// <summary>服务商级请求头（可为空）。</summary>
    public IReadOnlyList<HeaderPair>? ProviderHeaders { get; init; }
}

/// <summary>settings.saveNetwork 结果。</summary>
public sealed record SettingsSaveNetworkResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>备注。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ============================ settings.probeModels ============================

/// <summary>settings.probeModels 参数。</summary>
public sealed record SettingsProbeModelsParams
{
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>API Key（可为空）。</summary>
    public string? ApiKey { get; init; }
}

/// <summary>settings.probeModels 结果。</summary>
public sealed record SettingsProbeModelsResult
{
    /// <summary>模型名列表。</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
}

// ============================ settings.saveExtraHeaders ============================

/// <summary>settings.saveExtraHeaders 参数。</summary>
public sealed record SettingsSaveExtraHeadersParams
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; }
}

/// <summary>settings.saveExtraHeaders 结果。</summary>
public sealed record SettingsSaveExtraHeadersResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ settings.mcp.get ============================

/// <summary>settings.mcp.get 结果。</summary>
public sealed record SettingsMcpGetResult
{
    /// <summary>配置文件路径。</summary>
    public string Path { get; init; } = "";
    /// <summary>文件内容。</summary>
    public string Text { get; init; } = "";
}

// ============================ settings.mcp.save ============================

/// <summary>settings.mcp.save 参数。</summary>
public sealed record SettingsMcpSaveParams
{
    /// <summary>文件内容。</summary>
    public string Text { get; init; } = "";
}

/// <summary>settings.mcp.save 结果。</summary>
public sealed record SettingsMcpSaveResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>备注。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}
