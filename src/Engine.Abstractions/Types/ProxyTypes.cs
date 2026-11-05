namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>代理种类（诊断/日志一律读 Kind，禁止以 ProxyUrl==null 判直连）。</summary>
public enum ProxyKind
{
    None = 0,
    Manual = 1,
    System = 2,
}

/// <summary>代理有效解析产物。System 不在 Resolve 期固化地址（运行时跟系统）。</summary>
public sealed record EffectiveProxy
{
    public ProxyKind Kind { get; init; } = ProxyKind.None;
    public string? Url { get; init; }

    public static EffectiveProxy None => new() { Kind = ProxyKind.None };
    public static EffectiveProxy System => new() { Kind = ProxyKind.System };
    public static EffectiveProxy Manual(string url) => new() { Kind = ProxyKind.Manual, Url = url };

    /// <summary>最终是否走代理（System 下保守标 true，UI 注明“跟随系统”）。</summary>
    public bool InUse => Kind != ProxyKind.None;

    public bool Equals(EffectiveProxy? other) =>
        other is not null && Kind == other.Kind &&
        string.Equals(Url ?? "", other.Url ?? "", StringComparison.Ordinal);
    public override int GetHashCode() => HashCode.Combine((int)Kind, Url ?? "");
}

/// <summary>ProxySource 快照口径。</summary>
public static class ProxySources
{
    public const string GlobalDirect = "global-direct";
    public const string GlobalManual = "global-manual";
    public const string GlobalSystem = "global-system";
    public const string ProviderDirect = "provider-direct";
    public const string ProviderProxy = "provider-proxy";
    public const string BypassLocal = "bypass-local";
}
