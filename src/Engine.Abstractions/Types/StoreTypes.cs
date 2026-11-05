namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>
/// Responses 协议的 store 三档语义（P26）。
/// 归属：由 Provider 级配置 response_store 决定，见 ConfigStore.Resolve。
/// 位置在 Abstractions：Storage（配置解析）与 Core（请求体）都要用，Storage 禁止引用 Core。
/// </summary>
public enum StoreMode
{
    /// <summary>缺省：请求体**不含** store 键，由端点自己决定默认行为（最大兼容）。</summary>
    Default = 0,
    /// <summary>显式 store=true：服务端持久化，previous_response_id 链可增量。</summary>
    True = 1,
    /// <summary>显式 store=false：显式声明不持久化（P13 定案口径）。</summary>
    False = 2,
}

/// <summary>Provider response_store 三档取值（与前端 PROVIDER_RESPONSE_STORES 必须一致，全小写）。</summary>
public static class ResponseStores
{
    public const string Default = "default";     // 缺省：省略 store 键
    public const string True = "true";        // 显式 store=true
    public const string False = "false";       // 显式 store=false

    public static bool IsValid(string? v) =>
        v is Default or True or False;           // 严格小写匹配

    /// <summary>归一化：非法/缺失一律回落 Default（绝不回落 True）。</summary>
    public static string Normalize(string? v) =>
        v is True or False ? v! : Default;

    /// <summary>字符串 → StoreMode 枚举（非法值回落 Default，绝不抛）。</summary>
    public static StoreMode ToStoreMode(string? v) => v switch
    {
        True => StoreMode.True,
        False => StoreMode.False,
        _ => StoreMode.Default,
    };

    public static string FromStoreMode(StoreMode m) => m switch
    {
        StoreMode.True => True,
        StoreMode.False => False,
        _ => Default,
    };
}
