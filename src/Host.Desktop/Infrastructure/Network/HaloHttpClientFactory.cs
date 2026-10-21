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

/// <summary>
/// 全局唯一网络请求入口与代理连接池中枢（ProxyHub）。
/// 严禁业务代码私自 new HttpClient()，所有对外 HTTP/HTTPS 请求统一通过本工厂创建。
/// </summary>
public sealed class HaloHttpClientFactory : IDisposable
{
    private sealed record CachedHandlerEntry(
        SocketsHttpHandler Handler,
        EffectiveProxyDescriptor Descriptor,
        DateTime CreatedAtUtc
    );

    private readonly ConcurrentDictionary<string, CachedHandlerEntry> _handlerPool =
        new(StringComparer.Ordinal);
    private readonly object _settingsLock = new();
    private GlobalNetworkSettings _globalSettings;
    private bool _disposed;

    public HaloHttpClientFactory(GlobalNetworkSettings? initialSettings = null)
    {
        _globalSettings = initialSettings?.Clone() ?? new GlobalNetworkSettings();
    }

    /// <summary>
    /// 获取当前生效的全局网络代理配置副本
    /// </summary>
    public GlobalNetworkSettings GetGlobalSettings()
    {
        lock (_settingsLock)
        {
            return _globalSettings.Clone();
        }
    }

    /// <summary>
    /// 热更新全局网络代理配置（新请求立即按新代理指纹路由）
    /// </summary>
    public void UpdateGlobalSettings(GlobalNetworkSettings newSettings)
    {
        if (newSettings == null)
        {
            throw new ArgumentNullException(nameof(newSettings));
        }

        lock (_settingsLock)
        {
            _globalSettings = newSettings.Clone();
        }
    }

    /// <summary>
    /// 全局唯一获取 HttpClient 的标准入口
    /// </summary>
    public HttpClient CreateClient(
        ProviderConfig? provider = null,
        GlobalNetworkSettings? overrideGlobalSettings = null
    )
    {
        var global = overrideGlobalSettings ?? GetGlobalSettings();
        var effectiveProxy = ResolveEffectiveProxy(provider, global);
        string fingerprint = effectiveProxy.ComputeFingerprint();

        var entry = _handlerPool.GetOrAdd(fingerprint, _ => CreateHandlerEntry(effectiveProxy));
        // need22-P1 T3：单次 HTTP 超时上限 120s（hung 网关不再跟随进程；配置更大值同样钳制到 120s）。
        int requestTimeoutSec =
            global.DefaultRequestTimeoutSeconds <= 0 ? 120 : global.DefaultRequestTimeoutSeconds;
        var client = new HttpClient(entry.Handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSec, 5, 120))
        };
        return client;
    }

    /// <summary>
    /// 计算指定 Provider 在当前全局网络设置下最终生效的代理描述符（严格遵循级联代理解析决策矩阵）
    /// </summary>
    public EffectiveProxyDescriptor ResolveEffectiveProxy(
        ProviderConfig? provider = null,
        GlobalNetworkSettings? overrideGlobalSettings = null
    )
    {
        var global = overrideGlobalSettings ?? GetGlobalSettings();
        int connectTimeout = Math.Clamp(global.DefaultConnectTimeoutSeconds, 3, 120);

        // 1. Provider 显式指定 Custom 独立代理
        if (
            provider != null
            && provider.ProxyMode == ProviderProxyMode.Custom
            && provider.CustomProxy != null
        )
        {
            return BuildCustomDescriptor(provider.CustomProxy, connectTimeout);
        }

        // 2. Provider 显式指定 None 强制直连（如本地 Ollama / 内网 vLLM）
        if (provider != null && provider.ProxyMode == ProviderProxyMode.None)
        {
            return new EffectiveProxyDescriptor(
                Kind: EffectiveProxyKind.None,
                ConnectTimeoutSeconds: connectTimeout
            );
        }

        // 3. Provider 为 Inherit（或未指定 Provider），继承全局代理设置
        return global.Mode switch
        {
            GlobalProxyMode.None
                => new EffectiveProxyDescriptor(
                    Kind: EffectiveProxyKind.None,
                    ConnectTimeoutSeconds: connectTimeout
                ),

            GlobalProxyMode.Custom => BuildCustomDescriptor(global.CustomProxy, connectTimeout),

            _ => ResolveSystemProxyDescriptor(provider?.BaseUrl, connectTimeout)
        };
    }

    /// <summary>
    /// 测试代理连通性与首字节延迟
    /// </summary>
    public async Task<ProxyConnectivityTestResult> TestProxyConnectivityAsync(
        string? targetUrl = null,
        GlobalNetworkSettings? overrideGlobalSettings = null,
        ProviderConfig? provider = null,
        string? sessionId = null,
        string? workspacePath = null,
        CancellationToken cancellationToken = default
    )
    {
        string resolvedTargetUrl = ResolveProbeTargetUrl(targetUrl, provider);
        var global = overrideGlobalSettings ?? GetGlobalSettings();
        var descriptor = ResolveEffectiveProxy(provider, global);
        string effectiveSummary = descriptor.ToDisplaySummary();

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateClient(provider, global);
            client.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(global.DefaultConnectTimeoutSeconds, 5, 30)
            );

            using var request = new HttpRequestMessage(HttpMethod.Get, resolvedTargetUrl);
            request.Headers.UserAgent.ParseAdd("HaloAgent/1.0 (Win7-Win11; CefSharp109)");

            // 若传入了 Provider，自动附带 Authorization 与自定义请求头，以便同时验证自定义 Header 与上游网关
            if (provider != null)
            {
                string apiKey =
                    !string.IsNullOrWhiteSpace(provider.ApiKey) && !provider.ApiKey.Contains("****")
                        ? provider.ApiKey
                        : SecretProtector.Unprotect(provider.ApiKeyEncrypted);

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var headerCtx = HeaderVariableResolver.CreateContext(
                    workspacePath: workspacePath ?? Environment.CurrentDirectory,
                    sessionId: sessionId ?? "probe-session",
                    providerId: provider.Id,
                    providerName: provider.Name,
                    modelId: provider.Models.FirstOrDefault()?.Id ?? "probe-model"
                );

                HeaderVariableResolver.ApplyCustomHeaders(request, provider, headerCtx);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();
            int statusCode = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                return new ProxyConnectivityTestResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    EffectiveProxyUsed = effectiveSummary,
                    TargetUrl = resolvedTargetUrl,
                    DiagnosticCategory = "ProxyAuthRequired",
                    ErrorMessage = "代理服务器需要身份验证或用户名/密码错误 (HTTP 407 Proxy Authentication Required)"
                };
            }

            // 上游返回 2xx 或 401/403/404（未带 Key 访问 /v1/models）均证明网络与代理链路已打通
            bool networkReachable = statusCode >= 200 && statusCode < 500;
            return new ProxyConnectivityTestResult
            {
                Success = networkReachable,
                StatusCode = statusCode,
                ElapsedMs = sw.ElapsedMilliseconds,
                EffectiveProxyUsed = effectiveSummary,
                TargetUrl = resolvedTargetUrl,
                DiagnosticCategory = networkReachable ? "OK" : "UpstreamServerError",
                ErrorMessage = networkReachable ? null : $"上游服务器返回异常状态码: HTTP {statusCode}"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var (category, friendlyError) = ClassifyNetworkException(ex, descriptor);
            HaloLogger.App.Warn(
                $"Proxy connectivity probe failed -> Target: {resolvedTargetUrl}, Proxy: {effectiveSummary}, Category: {category}, Error: {ex.Message}"
            );

            return new ProxyConnectivityTestResult
            {
                Success = false,
                StatusCode = null,
                ElapsedMs = sw.ElapsedMilliseconds,
                EffectiveProxyUsed = effectiveSummary,
                TargetUrl = resolvedTargetUrl,
                DiagnosticCategory = category,
                ErrorMessage = friendlyError
            };
        }
    }

    private static string ResolveProbeTargetUrl(string? targetUrl, ProviderConfig? provider)
    {
        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            string trimmed = targetUrl.Trim();
            return trimmed.Contains("://") ? trimmed : $"https://{trimmed}";
        }

        if (provider != null && !string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return HeaderVariableResolver.ResolveModelsEndpointUrl(provider.BaseUrl);
        }

        return "https://api.openai.com/v1/models";
    }

    private static EffectiveProxyDescriptor BuildCustomDescriptor(
        ManualProxyConfig custom,
        int connectTimeout
    )
    {
        string? decryptedPwd =
            !string.IsNullOrEmpty(custom.Password) && !custom.Password.Contains("****")
                ? custom.Password
                : SecretProtector.Unprotect(custom.PasswordEncrypted);

        return new EffectiveProxyDescriptor(
            Kind: EffectiveProxyKind.Custom,
            Scheme: EffectiveProxyDescriptor.NormalizeScheme(custom.Scheme),
            Host: string.IsNullOrWhiteSpace(custom.Host) ? "127.0.0.1" : custom.Host.Trim(),
            Port: custom.Port is > 0 and <= 65535 ? custom.Port : 7890,
            RequiresAuth: custom.RequiresAuth,
            Username: custom.Username?.Trim(),
            DecryptedPassword: decryptedPwd,
            BypassList: custom.BypassList,
            BypassOnLocal: custom.BypassOnLocal,
            ConnectTimeoutSeconds: connectTimeout
        );
    }

    private static EffectiveProxyDescriptor ResolveSystemProxyDescriptor(
        string? targetUrl,
        int connectTimeout
    )
    {
        string probeUrl =
            !string.IsNullOrWhiteSpace(targetUrl) && targetUrl.Contains("://")
                ? targetUrl
                : "https://api.openai.com";

        var snapshot = SystemProxyInspector.InspectCurrentSystemProxy(probeUrl);
        return new EffectiveProxyDescriptor(
            Kind: EffectiveProxyKind.System,
            BypassList: snapshot.BypassOverride,
            SystemProxyUri: snapshot.IsEnabled ? snapshot.EffectiveHttpProxyUri : null,
            ConnectTimeoutSeconds: connectTimeout
        );
    }

    private CachedHandlerEntry CreateHandlerEntry(EffectiveProxyDescriptor descriptor)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(
                Math.Clamp(descriptor.ConnectTimeoutSeconds, 3, 120)
            ),
            AutomaticDecompression =
                DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        };

        switch (descriptor.Kind)
        {
            case EffectiveProxyKind.None:
                // 强制不使用代理，彻底隔离操作系统全局代理
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case EffectiveProxyKind.System:
                if (!string.IsNullOrWhiteSpace(descriptor.SystemProxyUri))
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(
                        new Uri(descriptor.SystemProxyUri),
                        BypassOnLocal: true
                    );
                }
                else
                {
                    // 当前系统未开启代理时直接连接
                    handler.UseProxy = false;
                    handler.Proxy = null;
                }
                break;

            case EffectiveProxyKind.Custom:
                handler.UseProxy = true;
                string scheme = EffectiveProxyDescriptor.NormalizeScheme(descriptor.Scheme);
                var proxyUri = new Uri($"{scheme}://{descriptor.Host}:{descriptor.Port}");
                string[] bypassRegexes = ParseBypassRegexList(descriptor.BypassList);

                WebProxy webProxy;
                try
                {
                    webProxy = new WebProxy(proxyUri, descriptor.BypassOnLocal, bypassRegexes);
                }
                catch
                {
                    webProxy = new WebProxy(proxyUri, descriptor.BypassOnLocal);
                }

                if (descriptor.RequiresAuth && !string.IsNullOrWhiteSpace(descriptor.Username))
                {
                    webProxy.Credentials = new NetworkCredential(
                        descriptor.Username,
                        descriptor.DecryptedPassword ?? string.Empty
                    );
                }

                handler.Proxy = webProxy;
                break;
        }

        return new CachedHandlerEntry(handler, descriptor, DateTime.UtcNow);
    }

    /// <summary>
    /// 将用户填写的分号/逗号分隔通配符列表（如 "localhost;127.*;192.168.*"）转换为 WebProxy 所需的合法正则表达式数组
    /// </summary>
    public static string[] ParseBypassRegexList(string? bypassList)
    {
        if (string.IsNullOrWhiteSpace(bypassList))
        {
            return Array.Empty<string>();
        }

        var items = bypassList.Split(
            new[] { ';', ',' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        var result = new List<string>();

        foreach (var raw in items)
        {
            if (
                string.IsNullOrWhiteSpace(raw)
                || raw.Equals("<local>", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            // 将通配符 * 转为 .*，并对其他特殊正则字符转义
            string pattern = Regex.Escape(raw).Replace(@"\*", ".*").Replace(@"\?", ".");
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                result.Add(pattern);
            }
            catch
            {
                // 忽略非法通配符项
            }
        }

        return result.ToArray();
    }

    private static (string Category, string FriendlyMessage) ClassifyNetworkException(
        Exception ex,
        EffectiveProxyDescriptor descriptor
    )
    {
        if (ex is TaskCanceledException or TimeoutException)
        {
            return ("Timeout", $"连接超时（超过 {descriptor.ConnectTimeoutSeconds} 秒），请检查代理服务器或目标地址是否可达");
        }

        if (FindInnerException<AuthenticationException>(ex) is { } authEx)
        {
            return ("TlsError", $"TLS/SSL 安全握手失败: {authEx.Message}");
        }

        if (FindInnerException<SocketException>(ex) is { } sockEx)
        {
            if (sockEx.SocketErrorCode == SocketError.ConnectionRefused)
            {
                string targetDesc =
                    descriptor.Kind == EffectiveProxyKind.None
                        ? "目标服务器"
                        : $"代理服务器 ({descriptor.ToDisplaySummary()})";
                return (
                    "ProxyConnectionRefused",
                    $"{targetDesc} 拒绝连接 (SocketError: ConnectionRefused / 10061)，请确认代理软件已启动且端口正确"
                );
            }

            if (sockEx.SocketErrorCode == SocketError.HostNotFound)
            {
                return ("DnsError", $"域名解析失败 (HostNotFound): {sockEx.Message}");
            }

            return ("SocketError", $"底层套接字网络异常 ({sockEx.SocketErrorCode}): {sockEx.Message}");
        }

        if (
            ex is HttpRequestException httpEx
            && httpEx.Message.Contains("407", StringComparison.OrdinalIgnoreCase)
        )
        {
            return (
                "ProxyAuthRequired",
                $"代理认证失败 (407 Proxy Authentication Required): {httpEx.Message}"
            );
        }

        return ("NetworkError", $"网络请求失败: {ex.Message}");
    }

    private static T? FindInnerException<T>(Exception? ex)
        where T : Exception
    {
        while (ex != null)
        {
            if (ex is T matched)
            {
                return matched;
            }
            ex = ex.InnerException;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var kv in _handlerPool)
        {
            try
            {
                kv.Value.Handler.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        _handlerPool.Clear();
    }
}
