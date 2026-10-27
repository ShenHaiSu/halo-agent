using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ state.snapshot ============================
// state.snapshot 无参数，结果为 StateSnapshot（见 Types/StateSnapshot.cs）。

// ============================ state.setMode ============================

/// <summary>state.setMode 参数。</summary>
public sealed record StateSetModeParams
{
    /// <summary>新权限模式。</summary>
    public PermissionMode Mode { get; init; } = PermissionMode.Ask;
}

/// <summary>state.setMode 结果。</summary>
public sealed record StateSetModeResult
{
    /// <summary>生效后的权限模式。</summary>
    public PermissionMode Mode { get; init; } = PermissionMode.Ask;
    /// <summary>界面标签。</summary>
    public string Label { get; init; } = "";
}

// ============================ state.setPersona ============================

/// <summary>state.setPersona 参数。</summary>
public sealed record StateSetPersonaParams
{
    /// <summary>人格名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>state.setPersona 结果。</summary>
public sealed record StateSetPersonaResult
{
    /// <summary>生效后的人格名。</summary>
    public string Name { get; init; } = "";
}

// ============================ state.setProvider ============================

/// <summary>state.setProvider 参数。</summary>
public sealed record StateSetProviderParams
{
    /// <summary>服务商名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>state.setProvider 结果。</summary>
public sealed record StateSetProviderResult
{
    /// <summary>服务商名。</summary>
    public string Provider { get; init; } = "";
    /// <summary>模型名。</summary>
    public string Model { get; init; } = "";
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
}

// ============================ state.setMaxTurns ============================

/// <summary>state.setMaxTurns 参数。</summary>
public sealed record StateSetMaxTurnsParams
{
    /// <summary>最大轮数（≥0）。</summary>
    public int Value { get; init; }
}

/// <summary>state.setMaxTurns 结果。</summary>
public sealed record StateSetMaxTurnsResult
{
    /// <summary>生效后的最大轮数。</summary>
    public int MaxTurns { get; init; }
}

// ============================ state.setRetry ============================

/// <summary>state.setRetry 参数。</summary>
public sealed record StateSetRetryParams
{
    /// <summary>重试次数（0~8）。</summary>
    public int Value { get; init; }
}

/// <summary>state.setRetry 结果。</summary>
public sealed record StateSetRetryResult
{
    /// <summary>生效后的重试次数。</summary>
    public int Retry { get; init; }
}
