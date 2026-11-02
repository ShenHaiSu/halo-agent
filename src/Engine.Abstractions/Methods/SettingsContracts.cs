using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ settings.get ============================

 /// <summary>settings.get 结果（P10 新整树：一次拿全）。P18：network 取代 useProxy + effective 下发。P22：行为页 compactKeepTail/autoCompactOnLimit。</summary>
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
     public int CompactKeepTail { get; init; } = 2;
     public bool AutoCompactOnLimit { get; init; }
     public string DataDir { get; init; } = "";
     public string ConfigPath { get; init; } = "";
 }

public sealed record ProviderView
{
    public string Id { get; init; } = "";
    /// <summary>别称（P14 别名原则）：null/空/==Id(Ordinal) 均视为无别名；有别名时为 trim 后的别名。</summary>
    public string? DisplayName { get; init; }
    public string BaseUrl { get; init; } = "";
    public bool ApiKeySet { get; init; }
    public bool HeadersEnabled { get; init; } = true;
    public IReadOnlyList<HeaderPair> Headers { get; init; } = Array.Empty<HeaderPair>();
    /// <summary>P18：follow | proxy | direct。</summary>
    public string Network { get; init; } = "follow";
    /// <summary>兼容旧字段：Network != direct（读兼容；新客户端读 Network）。</summary>
    [Obsolete("Use Network")]
    public bool UseProxy { get; init; } = true;
    public ProviderEffective? Effective { get; init; }
    public int ModelCount { get; init; }
    public IReadOnlyList<ModelView> Models { get; init; } = Array.Empty<ModelView>();
}

/// <summary>P18：单个 Provider 生效代理（供黄条/卡片 tag 显示）。</summary>
public sealed record ProviderEffective
{
    public bool InUse { get; init; }
    public string Source { get; init; } = "";
    public string? MaskedUrl { get; init; }
    public string? Warning { get; init; }
}

public sealed record ModelView
{
    public string Id { get; init; } = "";
    /// <summary>别称（P14 别名原则）：null/空/==Id(Ordinal) 均视为无别名；有别名时为 trim 后的别名。</summary>
    public string? DisplayName { get; init; }
    public string Protocol { get; init; } = "chat";
    public int? ContextWindow { get; init; }
    public int? MaxTokens { get; init; }
    public bool AcceptsImages { get; init; }
    public bool IncludeReasoningHistory { get; init; } = true;
}

/// <summary>代理设置。P18：system | direct | manual。</summary>
public sealed record ProxySettings
{
    /// <summary>“system” | “direct” | “manual”。</summary>
    public string Mode { get; init; } = "system";
    /// <summary>代理地址（可为空）。</summary>
    public string? Url { get; init; }
}

// ============================ settings.saveProviders ============================

public sealed record SettingsSaveProvidersParams
{
    public IReadOnlyDictionary<string, ProviderSaveEntry>? Providers { get; init; }
    /// <summary>二次确认覆盖旧结构/损坏配置。前端仅在 migrationRequired 或 config.overwriteRequired 两步确认后传 true；默认 false 仍拒绝覆盖。</summary>
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
    /// <summary>P18：follow | proxy | direct。</summary>
    public string? Network { get; init; }
    /// <summary>旧 useProxy 兼容一个版本（network 缺席时回落）。</summary>
    [Obsolete("Use Network")]
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

 // ============================ settings.saveBehavior ============================
 
 /// <summary>P22 行为页：5 项全局行为配置（maxTurns/retry/persona/compactKeepTail/autoCompactOnLimit）。</summary>
 public sealed record SettingsSaveBehaviorParams
 {
     public int? MaxTurns { get; init; }
     public int? Retry { get; init; }
     public string? Persona { get; init; }
     public int? CompactKeepTail { get; init; }
     public bool? AutoCompactOnLimit { get; init; }
 }
 
 /// <summary>P22 settings.saveBehavior 结果（与 saveNetwork 对齐，只有 ok/warnings）。</summary>
 public sealed record SettingsSaveBehaviorResult
 {
     public bool Ok { get; init; }
     public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
 }
 
 // ============================ settings.saveNetwork ============================
 
 /// <summary>settings.saveNetwork 参数（P18：全局三档 system/direct/manual + 重试）。P22：retry 已搬家至 saveBehavior，后端保留读兼容。</summary>
 public sealed record SettingsSaveNetworkParams
 {
     /// <summary>代理模式：“system” | “direct” | “manual”（可为空）。</summary>
     public string? Mode { get; init; }
     /// <summary>代理地址（可为空）。</summary>
     public string? Url { get; init; }
     /// <summary>重试次数（可为空；已搬家至 saveBehavior，此处为兼容保留）。</summary>
     public int? Retry { get; init; }
 }
 
 /// <summary>settings.saveNetwork 结果（P18：加 Warnings）。</summary>
 public sealed record SettingsSaveNetworkResult
 {
     /// <summary>是否成功。</summary>
     public bool Ok { get; init; }
     /// <summary>备注。</summary>
     public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
     /// <summary>P18：代理组合降级 warning（与 saveProviders 同名，不复用 Notes）。</summary>
     public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
 }

// ============================ settings.probeModels ============================

/// <summary>settings.probeModels 参数（P18：network 三选项，读时兼容旧 useProxy）。</summary>
public sealed record SettingsProbeModelsParams
{
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>API Key（可为空，为空时回落库中已存 key；clearKey=true 时不回落）。</summary>
    public string? ApiKey { get; init; }
    public string? ProviderId { get; init; }
    public IReadOnlyList<HeaderPair>? Headers { get; init; }
    /// <summary>P18：follow | proxy | direct（试算优先）。</summary>
    public string? Network { get; init; }
    /// <summary>旧 useProxy 兼容（network 缺席时回落）。</summary>
    [Obsolete("Use Network")]
    public bool? UseProxy { get; init; }
    /// <summary>用户已点“清除”但未保存：探测不回落旧 key。</summary>
    public bool ClearKey { get; init; }
}

/// <summary>settings.probeModels 结果（P18：加 via/latencyMs）。</summary>
public sealed record SettingsProbeModelsResult
{
    /// <summary>模型名列表。</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    /// <summary>P18：本次探测走的代理。</summary>
    public ProbeVia? Via { get; init; }
    /// <summary>P18：全程耗时毫秒（含 responses ping）。</summary>
    public long LatencyMs { get; init; }
}

/// <summary>P18：probe 生效代理回包。</summary>
public sealed record ProbeVia
{
    public string Source { get; init; } = "";
    public string? MaskedUrl { get; init; }
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
