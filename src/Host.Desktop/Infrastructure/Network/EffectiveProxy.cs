using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Network;

/// <summary>
/// 最终生效的代理类别
/// </summary>
public enum EffectiveProxyKind
{
    None = 0,
    System = 1,
    Custom = 2
}

/// <summary>
/// 经级联规则计算后的生效代理描述符
/// </summary>
public sealed record EffectiveProxyDescriptor(
    EffectiveProxyKind Kind,
    string? Scheme = null,
    string? Host = null,
    int Port = 0,
    bool RequiresAuth = false,
    string? Username = null,
    string? DecryptedPassword = null,
    string? BypassList = null,
    bool BypassOnLocal = true,
    string? SystemProxyUri = null,
    int ConnectTimeoutSeconds = 15
)
{
    /// <summary>
    /// 计算代理配置指纹（用于池化缓存 SocketsHttpHandler，配置变更后指纹自动变化并秒级切换）
    /// </summary>
    public string ComputeFingerprint()
    {
        string raw =
            $"{Kind}|{NormalizeScheme(Scheme)}|{Host}:{Port}|{RequiresAuth}|{Username}|{DecryptedPassword}|{BypassList}|{BypassOnLocal}|{SystemProxyUri}|{ConnectTimeoutSeconds}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>
    /// 生成可读的生效代理摘要说明（例如 "DIRECT (No Proxy)", "SYSTEM (http://127.0.0.1:7890)", "CUSTOM (socks5://127.0.0.1:10808)"）
    /// </summary>
    public string ToDisplaySummary()
    {
        return Kind switch
        {
            EffectiveProxyKind.None => "DIRECT (No Proxy)",
            EffectiveProxyKind.System
                => !string.IsNullOrWhiteSpace(SystemProxyUri)
                    ? $"SYSTEM ({SystemProxyUri})"
                    : "DIRECT (System No Proxy)",
            EffectiveProxyKind.Custom => $"CUSTOM ({NormalizeScheme(Scheme)}://{Host}:{Port})",
            _ => "DIRECT (No Proxy)"
        };
    }

    public static string NormalizeScheme(string? scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return "http";
        }

        string cleaned = scheme.Trim().ToLowerInvariant();
        if (cleaned.EndsWith("://", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^3];
        }

        return cleaned switch
        {
            "http" or "https" or "socks5" or "socks4" or "socks4a" => cleaned,
            _ => "http"
        };
    }
}
