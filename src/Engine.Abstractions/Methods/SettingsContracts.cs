using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ settings.get ============================

/// <summary>settings.get 结果（P10 新整树：一次拿全）。</summary>
public sealed record SettingsGetResult
{
    public string ActiveProvider { get; init; } = "";
    public string ActiveModel { get; init; } = "";
    public bool MigrationRequired { get; init; }
    public IReadOnlyList<ProviderView> Providers { get; init; } = Array.Empty<ProviderView>();
    public ProxySettings? Proxy { get; init; }
    public int Retry { get; init; }
    public int MaxTurns { get; init; }
    public string Persona { get; init; } = "";
    public string DataDir { get; init; } = "";
    public string ConfigPath { get; init; } = "";
}

public sealed record ProviderView
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public bool ApiKeySet { get; init; }
    public bool HeadersEnabled { get; init; } = true;
    public IReadOnlyList<HeaderPair> Headers { get; init; } = Array.Empty<HeaderPair>();
    public bool UseProxy { get; init; } = true;
    public int ModelCount { get; init; }
    public IReadOnlyList<ModelView> Models { get; init; } = Array.Empty<ModelView>();
}

public sealed record ModelView
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Protocol { get; init; } = "chat";
    public int? ContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    public bool AcceptsImages { get; init; }
    public bool IncludeReasoningHistory { get; init; } = true;
}

/// <summary>代理设置。</summary>
public sealed record ProxySettings
{
    /// <summary>“auto” | “direct” | “manual”。</summary>
    public string Mode { get; init; } = "auto";
    /// <summary>代理地址（可为空）。</summary>
    public string? Url { get; init; }
}

// ============================ settings.saveProviders ============================

public sealed record SettingsSaveProvidersParams
{
    public IReadOnlyDictionary<string, ProviderSaveEntry>? Providers { get; init; }
    /// <summary>二次确认覆盖旧结构/损坏配置。前端仅在 migrationRequired 两步确认后传 true；默认 false 仍拒绝覆盖。</summary>
    public bool Force { get; init; }
}

public sealed record ProviderSaveEntry
{
    public string? DisplayName { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public bool ClearKey { get; init; }
    public bool? HeadersEnabled { get; init; }
    public IReadOnlyList<HeaderPair>? Headers { get; init; }
    public bool? UseProxy { get; init; }
    public IReadOnlyList<ModelSaveEntry>? Models { get; init; }
}

public sealed record ModelSaveEntry
{
    public string Id { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Protocol { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    public bool? AcceptsImages { get; init; }
    public bool? IncludeReasoningHistory { get; init; }
}

public sealed record SettingsSaveProvidersResult
{
    public bool Ok { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SettingsFieldError> Errors { get; init; } = Array.Empty<SettingsFieldError>();
}

public sealed record SettingsFieldError
{
    public string Provider { get; init; } = "";
    public string? Model { get; init; }
    public string Field { get; init; } = "";
    public string Message { get; init; } = "";
}

// ============================ settings.setActive ============================

public sealed record SettingsSetActiveParams
{
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
}

public sealed record SettingsSetActiveResult
{
    public bool Ok { get; init; }
}

// ============================ settings.saveNetwork ============================

/// <summary>settings.saveNetwork 参数（P10 瘦身：只剩全局代理+重试）。</summary>
public sealed record SettingsSaveNetworkParams
{
    /// <summary>代理模式：“auto” | “direct” | “manual”（可为空）。</summary>
    public string? Mode { get; init; }
    /// <summary>代理地址（可为空）。</summary>
    public string? Url { get; init; }
    /// <summary>重试次数（可为空）。</summary>
    public int? Retry { get; init; }
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

/// <summary>settings.probeModels 参数（P10 加参：带头+走对代理）。</summary>
public sealed record SettingsProbeModelsParams
{
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>API Key（可为空）。</summary>
    public string? ApiKey { get; init; }
    public string? ProviderId { get; init; }
    public IReadOnlyList<HeaderPair>? Headers { get; init; }
    public bool? UseProxy { get; init; }
}

/// <summary>settings.probeModels 结果。</summary>
public sealed record SettingsProbeModelsResult
{
    /// <summary>模型名列表。</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
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
