# Stage 02 - 02：统一网络请求代理中枢（ProxyHub）设计与实现方案

> **所属阶段**：Stage 02
> **核心对应需求**：需求 #1（统一的网络请求代理配置以及入口：自动检查当前系统代理、手动配置、不使用代理）与需求 #7（Provider 级 proxy 默认继承系统配置）

---

## 一、 代理配置领域模型设计（`ProxySettings.cs`）

```csharp
namespace Host.Desktop.Core.Models;

/// <summary>
/// 全局代理模式枚举
/// </summary>
public enum GlobalProxyMode
{
    System = 0, // 自动检测并使用当前操作系统代理
    Custom = 1, // 使用用户手动配置的代理
    None = 2    // 不使用任何代理（强制直连，绕过系统代理）
}

/// <summary>
/// Provider 级代理策略枚举（默认 Inherit）
/// </summary>
public enum ProviderProxyMode
{
    Inherit = 0, // 默认：继承全局代理设置
    None = 1,    // 强制直连（例如本地 Ollama 127.0.0.1 不走全局代理）
    Custom = 2   // 使用该 Provider 专属的手动代理
}

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
    public string? PasswordEncrypted { get; set; }
    /// <summary>
    /// 忽略代理的目标地址列表，分号或逗号分隔，默认跳过本地回环
    /// </summary>
    public string BypassList { get; set; } = "localhost;127.*;10.*;192.168.*";
    public bool BypassOnLocal { get; set; } = true;
}

public sealed class GlobalNetworkSettings
{
    public GlobalProxyMode Mode { get; set; } = GlobalProxyMode.System;
    public ManualProxyConfig CustomProxy { get; set; } = new();
    public int DefaultConnectTimeoutSeconds { get; set; } = 15;
    public int DefaultRequestTimeoutSeconds { get; set; } = 300;
}
```

---

## 二、 系统代理自动检测器（`SystemProxyInspector.cs`）

在 Windows 7 / 10 / 11 上，主流代理工具（Clash、v2rayN、Fiddler、企业域控代理）均通过修改当前用户的 WinINet 注册表项生效。为了在前端 UI 中向用户直观展示**“当前检测到的系统代理状态”**，实现双轨探测机制：

### 1. 注册表 + `WebRequest.GetSystemWebProxy()` 联合探测
```csharp
using Microsoft.Win32;
using System.Net;

namespace Host.Desktop.Infrastructure.Network;

public sealed record SystemProxySnapshot(
    bool IsEnabled,
    string? ProxyServerRaw,
    string? EffectiveHttpProxyUri,
    string? AutoConfigUrl,
    string? BypassOverride,
    DateTime InspectedAtUtc
);

public static class SystemProxyInspector
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static SystemProxySnapshot InspectCurrentSystemProxy(string probeTargetUrl = "https://api.openai.com")
    {
        bool proxyEnable = false;
        string? proxyServer = null;
        string? autoConfigUrl = null;
        string? proxyOverride = null;

        using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false))
        {
            if (key != null)
            {
                proxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
                proxyServer = key.GetValue("ProxyServer") as string;
                autoConfigUrl = key.GetValue("AutoConfigURL") as string;
                proxyOverride = key.GetValue("ProxyOverride") as string;
            }
        }

        // 同时调用系统原生动态解析（支持 PAC 脚本与分协议代理，如 http=127.0.0.1:7890;https=127.0.0.1:7890）
        string? effectiveUri = null;
        try
        {
            IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
            var targetUri = new Uri(probeTargetUrl);
            if (!systemProxy.IsBypassed(targetUri))
            {
                var resolved = systemProxy.GetProxy(targetUri);
                if (resolved != null && resolved != targetUri)
                {
                    effectiveUri = resolved.ToString().TrimEnd('/');
                }
            }
        }
        catch
        {
            // 忽略极端注册表格式异常，使用注册表原始值兜底
        }

        return new SystemProxySnapshot(
            IsEnabled: proxyEnable || !string.IsNullOrWhiteSpace(autoConfigUrl) || !string.IsNullOrWhiteSpace(effectiveUri),
            ProxyServerRaw: proxyServer,
            EffectiveHttpProxyUri: effectiveUri ?? (proxyEnable ? NormalizeProxyUri(proxyServer) : null),
            AutoConfigUrl: autoConfigUrl,
            BypassOverride: proxyOverride,
            InspectedAtUtc: DateTime.UtcNow
        );
    }

    private static string? NormalizeProxyUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // 处理 "http=127.0.0.1:7890;https=127.0.0.1:7890" 多协议格式或纯 "127.0.0.1:7890"
        if (raw.Contains('='))
        {
            var segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                var kv = seg.Split('=', 2);
                if (kv.Length == 2 && (kv[0].Equals("https", StringComparison.OrdinalIgnoreCase) || kv[0].Equals("http", StringComparison.OrdinalIgnoreCase)))
                {
                    return kv[1].Contains("://") ? kv[1] : $"http://{kv[1]}";
                }
            }
        }
        return raw.Contains("://") ? raw : $"http://{raw}";
    }
}
```

---

## 三、 统一网络请求入口与连接池（`HaloHttpClientFactory.cs`）

### 1. 级联代理解析决策矩阵（Resolution Matrix）
当某个 `ProviderConfig` 发起请求时，最终生效的代理规则计算逻辑如下：

```text
Provider.ProxyMode == Custom  ---> 采用 Provider.CustomProxy（手动配置）
Provider.ProxyMode == None    ---> 采用 直连模式（UseProxy = false）
Provider.ProxyMode == Inherit ---> 读取 GlobalNetworkSettings.Mode：
                                   ├── System ---> 采用当前系统代理（实时感知 WinINet 变更）
                                   ├── Custom ---> 采用 GlobalNetworkSettings.CustomProxy
                                   └── None   ---> 采用 直连模式（UseProxy = false）
```

### 2. `SocketsHttpHandler` 池化与生命周期管理
为防止每次请求创建新的 `SocketsHttpHandler` 引发端口耗尽（Socket Exhaustion），同时保证用户修改代理配置后**秒级生效**，设计按 **`EffectiveProxyFingerprint`（生效代理配置指纹）** 缓存 `HttpMessageInvoker` / `HttpClient`：

```csharp
public sealed class HaloHttpClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedHandlerEntry> _handlerPool = new();

    /// <summary>
    /// 全局唯一获取 HttpClient 的标准入口
    /// </summary>
    public HttpClient CreateClient(ProviderConfig? provider = null)
    {
        var effectiveProxy = ResolveEffectiveProxy(provider);
        string fingerprint = effectiveProxy.ComputeFingerprint();

        var entry = _handlerPool.GetOrAdd(fingerprint, _ => CreateHandlerEntry(effectiveProxy));
        var client = new HttpClient(entry.Handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(_globalSettings.DefaultRequestTimeoutSeconds)
        };
        return client;
    }

    private CachedHandlerEntry CreateHandlerEntry(EffectiveProxyDescriptor descriptor)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(_globalSettings.DefaultConnectTimeoutSeconds),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        switch (descriptor.Kind)
        {
            case EffectiveProxyKind.None:
                // 强制不使用代理，彻底隔离系统全局代理
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case EffectiveProxyKind.System:
                // 使用系统代理，若系统当前开启了代理则构造对应的 WebProxy
                var snapshot = SystemProxyInspector.InspectCurrentSystemProxy();
                if (snapshot.IsEnabled && !string.IsNullOrWhiteSpace(snapshot.EffectiveHttpProxyUri))
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(new Uri(snapshot.EffectiveHttpProxyUri), BypassOnLocal: true);
                }
                else
                {
                    handler.UseProxy = true;
                    handler.Proxy = HttpClient.DefaultProxy;
                }
                break;

            case EffectiveProxyKind.Custom:
                handler.UseProxy = true;
                var proxyUri = new Uri($"{descriptor.Scheme}://{descriptor.Host}:{descriptor.Port}");
                var webProxy = new WebProxy(proxyUri, descriptor.BypassOnLocal, ParseBypassRegexList(descriptor.BypassList));
                if (descriptor.RequiresAuth && !string.IsNullOrEmpty(descriptor.Username))
                {
                    webProxy.Credentials = new NetworkCredential(descriptor.Username, descriptor.DecryptedPassword);
                }
                handler.Proxy = webProxy;
                break;
        }

        return new CachedHandlerEntry(handler, DateTime.UtcNow);
    }
}
```

### 3. 代理连通性与延迟探测接口（`TestProxyConnectivityAsync`）
提供给前端“测试代理连接”按钮调用：
* 入参：`targetUrl`（默认可测用户当前选中的 Provider `baseUrl/models` 或通用连通性地址）、待测试的代理配置。
* 输出：
  * `success`: `bool`
  * `statusCode`: `int?`
  * `elapsedMs`: `long`（连接 + 首字节耗时）
  * `effectiveProxyUsed`: `string`（如 `"DIRECT (No Proxy)"`、`"SYSTEM (http://127.0.0.1:7890)"` 或 `"CUSTOM (socks5://127.0.0.1:10808)"`）
  * `errorMessage`: 若失败，精准区分是 **代理服务器拒绝连接（`SocketException: Connection refused`）**、**代理认证失败（`407 Proxy Authentication Required`）** 还是 **目标上游 TLS/超时错误**。
