using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>
/// 全局代理模式枚举
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlobalProxyMode
{
    System = 0, // 自动检测并使用当前操作系统代理
    Custom = 1, // 使用用户手动配置的代理
    None = 2    // 不使用任何代理（强制直连，绕过系统代理）
}

/// <summary>
/// Provider 级代理策略枚举（默认 Inherit）
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderProxyMode
{
    Inherit = 0, // 默认：继承全局代理设置
    None = 1,    // 强制直连（例如本地 Ollama 127.0.0.1 不走全局代理）
    Custom = 2   // 使用该 Provider 专属的手动代理
}

/// <summary>
/// 手动代理服务器配置
/// </summary>
public sealed class ManualProxyConfig
{
    /// <summary>
    /// 协议类型：http | https | socks5
    /// </summary>
    public string Scheme { get; set; } = "http";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 7890;

    public bool RequiresAuth { get; set; } = false;

    public string? Username { get; set; }

    /// <summary>
    /// DPAPI 加密后的代理密码 Base64 密文
    /// </summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>
    /// 前端传入的明文密码（仅用于更新输入，落盘前会被加密至 PasswordEncrypted 并置空）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    /// <summary>
    /// 供前端展示的密码脱敏掩码（落盘时置空）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordMasked { get; set; }

    /// <summary>
    /// 忽略代理的目标地址列表，分号或逗号分隔，默认跳过本地回环
    /// </summary>
    public string BypassList { get; set; } = "localhost;127.*;10.*;192.168.*";

    public bool BypassOnLocal { get; set; } = true;

    public ManualProxyConfig Clone()
    {
        return new ManualProxyConfig
        {
            Scheme = Scheme,
            Host = Host,
            Port = Port,
            RequiresAuth = RequiresAuth,
            Username = Username,
            PasswordEncrypted = PasswordEncrypted,
            Password = Password,
            PasswordMasked = PasswordMasked,
            BypassList = BypassList,
            BypassOnLocal = BypassOnLocal
        };
    }
}

/// <summary>
/// 全局网络与代理设置（持久化于 config/network.json）
/// </summary>
public sealed class GlobalNetworkSettings
{
    public GlobalProxyMode Mode { get; set; } = GlobalProxyMode.System;

    public ManualProxyConfig CustomProxy { get; set; } = new();

    public int DefaultConnectTimeoutSeconds { get; set; } = 15;

     /// <summary>need22-P1 T3：单次请求超时默认 120s（HttpClient.Timeout 同步钳制到 120s 上限）。</summary>
     public int DefaultRequestTimeoutSeconds { get; set; } = 120;

    public GlobalNetworkSettings Clone()
    {
        return new GlobalNetworkSettings
        {
            Mode = Mode,
            CustomProxy = CustomProxy?.Clone() ?? new ManualProxyConfig(),
            DefaultConnectTimeoutSeconds = DefaultConnectTimeoutSeconds,
            DefaultRequestTimeoutSeconds = DefaultRequestTimeoutSeconds
        };
    }
}

/// <summary>
/// 代理连通性测试结果
/// </summary>
public sealed class ProxyConnectivityTestResult
{
    public bool Success { get; set; }

    public int? StatusCode { get; set; }

    public long ElapsedMs { get; set; }

    public string EffectiveProxyUsed { get; set; } = "DIRECT (No Proxy)";

    public string TargetUrl { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public string DiagnosticCategory { get; set; } = "OK";
}
