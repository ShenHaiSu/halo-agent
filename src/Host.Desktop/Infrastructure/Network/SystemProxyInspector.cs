using System.Net;
using Microsoft.Win32;

namespace Host.Desktop.Infrastructure.Network;

/// <summary>
/// 操作系统代理状态实时快照
/// </summary>
public sealed record SystemProxySnapshot(
    bool IsEnabled,
    string? ProxyServerRaw,
    string? EffectiveHttpProxyUri,
    string? AutoConfigUrl,
    string? BypassOverride,
    DateTime InspectedAtUtc
);

/// <summary>
/// Windows 7 / 10 / 11 系统代理检测器。
/// 联合读取当前用户 WinINet 注册表项与系统动态代理配置，实时反映 Clash / v2rayN / Fiddler / 企业代理变更。
/// </summary>
public static class SystemProxyInspector
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static Func<string, SystemProxySnapshot>? _testOverrideInspector;

    /// <summary>
    /// 供自动化测试注入模拟的系统代理状态（避免测试过程修改宿主机真实注册表），传入 null 恢复真实检测
    /// </summary>
    public static void SetTestOverride(Func<string, SystemProxySnapshot>? overrideInspector)
    {
        _testOverrideInspector = overrideInspector;
    }

    /// <summary>
    /// 实时检测当前操作系统代理设置
    /// </summary>
    public static SystemProxySnapshot InspectCurrentSystemProxy(string probeTargetUrl = "https://api.openai.com")
    {
        var overrideFn = _testOverrideInspector;
        if (overrideFn != null)
        {
            return overrideFn(probeTargetUrl);
        }

        bool proxyEnable = false;
        string? proxyServer = null;
        string? autoConfigUrl = null;
        string? proxyOverride = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false);
            if (key != null)
            {
                proxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
                proxyServer = key.GetValue("ProxyServer") as string;
                autoConfigUrl = key.GetValue("AutoConfigURL") as string;
                proxyOverride = key.GetValue("ProxyOverride") as string;
            }
        }
        catch
        {
            // 忽略极端注册表访问受限场景
        }

        string? dynamicResolvedUri = null;
        if (proxyEnable || !string.IsNullOrWhiteSpace(autoConfigUrl))
        {
            try
            {
                IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
                var targetUri = new Uri(probeTargetUrl);
                if (!systemProxy.IsBypassed(targetUri))
                {
                    var resolved = systemProxy.GetProxy(targetUri);
                    if (resolved != null && resolved != targetUri)
                    {
                        dynamicResolvedUri = resolved.ToString().TrimEnd('/');
                    }
                }
            }
            catch
            {
                // 忽略极端注册表格式异常，使用注册表原始值兜底
            }
        }

        return BuildSnapshotFromValues(
            proxyEnable,
            proxyServer,
            autoConfigUrl,
            proxyOverride,
            dynamicResolvedUri);
    }

    /// <summary>
    /// 根据给定的注册表原始键值与动态解析地址生成规范化快照（同时支持单元测试直接验证各种代理格式）
    /// </summary>
    public static SystemProxySnapshot BuildSnapshotFromValues(
        bool proxyEnable,
        string? proxyServer,
        string? autoConfigUrl,
        string? proxyOverride,
        string? dynamicResolvedUri = null)
    {
        string? normalizedFromRegistry = proxyEnable ? NormalizeProxyUri(proxyServer) : null;
        string? effectiveUri = normalizedFromRegistry ?? (proxyEnable || !string.IsNullOrWhiteSpace(autoConfigUrl) ? dynamicResolvedUri : null);
        bool isEnabled = (proxyEnable && !string.IsNullOrWhiteSpace(effectiveUri))
                         || !string.IsNullOrWhiteSpace(autoConfigUrl)
                         || (!proxyEnable && !string.IsNullOrWhiteSpace(dynamicResolvedUri));

        return new SystemProxySnapshot(
            IsEnabled: isEnabled,
            ProxyServerRaw: proxyServer,
            EffectiveHttpProxyUri: isEnabled ? effectiveUri : null,
            AutoConfigUrl: autoConfigUrl,
            BypassOverride: proxyOverride,
            InspectedAtUtc: DateTime.UtcNow
        );
    }

    /// <summary>
    /// 规范化注册表 ProxyServer 字段（支持 "127.0.0.1:7890"、"http://127.0.0.1:7890" 以及 "http=127.0.0.1:7890;https=127.0.0.1:7890;socks=127.0.0.1:7891"）
    /// </summary>
    public static string? NormalizeProxyUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.Contains('='))
        {
            var segments = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? httpsCandidate = null;
            string? httpCandidate = null;
            string? socksCandidate = null;

            foreach (var seg in segments)
            {
                var kv = seg.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || string.IsNullOrWhiteSpace(kv[1]))
                {
                    continue;
                }

                string key = kv[0].ToLowerInvariant();
                string val = kv[1];
                if (key == "https")
                {
                    httpsCandidate = EnsureScheme(val, "http");
                }
                else if (key == "http")
                {
                    httpCandidate = EnsureScheme(val, "http");
                }
                else if (key is "socks" or "socks5" or "socks4")
                {
                    socksCandidate = EnsureScheme(val, "socks5");
                }
            }

            return httpsCandidate ?? httpCandidate ?? socksCandidate;
        }

        return EnsureScheme(trimmed, "http");
    }

    private static string EnsureScheme(string address, string defaultScheme)
    {
        string clean = address.Trim().TrimEnd('/');
        return clean.Contains("://") ? clean : $"{defaultScheme}://{clean}";
    }
}
