 using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Core.Net;

/// <summary>
/// HTTP 客户端工厂。**全项目唯一**建 HttpClient 的地方。
///
/// ============ 为什么统一 ============
/// 代理配置必须全链路一致。如果模型请求、网页抓取、更新下载各建各的，
/// 就会出现"模型能连、抓网页连不上"这种极难排查的问题。
///
/// ============ 超时分级 ============
///   模型请求：连接 10s / 总 600s
///   网页抓取：连接 10s / 总 30s
///   更新下载：连接 10s / 总 240s
/// 由调用方传 timeoutSeconds 覆盖。
///
/// P18-proxy02：单重载 Create(EffectiveProxy, …)。
///   None→UseProxy=false；Manual→WebProxy(url)+BypassList（含本地表 + NO_PROXY 复用）；
///   System→UseProxy=true, Proxy=null（由 OS 含 PAC 动态解析；无系统代理自然直连）。
/// System 旁路不靠工厂层（靠 Resolve 期预检 + WebFetch 逐 URL 预检）。
/// </summary>
public static class HttpClientFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static HttpClient Create(EffectiveProxy proxy, int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        if (proxy.Kind == ProxyKind.None)
        {
            handler.UseProxy = false;
        }
        else if (proxy.Kind == ProxyKind.Manual)
        {
            var webProxy = new System.Net.WebProxy((proxy.Url ?? "").Trim());
            var bypass = new List<string>(LocalBypassList());
            // 尊重 NO_PROXY 环境变量
            var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                          ?? Environment.GetEnvironmentVariable("no_proxy");
            if (!string.IsNullOrWhiteSpace(noProxy))
            {
                foreach (var host in noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    bypass.Add("^" + System.Text.RegularExpressions.Regex.Escape(host.Replace("*", "")) + ".*");
            }
            webProxy.BypassList = bypass.ToArray();
            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
        else
        {
            // System：交还 OS（含 PAC 动态解析）；无系统代理时自然直连
            handler.UseProxy = true;
            handler.Proxy = null;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        return client;
    }

    /// <summary>旧字符串重载（兼容保留）：非空合法 url→Manual，直连/system→对应档。</summary>
    [Obsolete("Use Create(EffectiveProxy, int)")]
    public static HttpClient Create(string? proxyUrl, int timeoutSeconds) =>
        Create(string.IsNullOrWhiteSpace(proxyUrl) ? EffectiveProxy.None : EffectiveProxy.Manual(proxyUrl.Trim()), timeoutSeconds);

    /// <summary>本地旁路表（WebProxy.BypassList 正则；大小写不敏感）。</summary>
    public static IReadOnlyList<string> LocalBypassList() => new[]
    {
        @"^localhost.*",
        @"^.*\.localhost$",
        @"^127\..*",
        @"^10\..*",
        @"^172\.(1[6-9]|2[0-9]|3[01])\..*",
        @"^192\.168\..*",
        @"^.*\.local$",
        @"^\[::1\].*",
    };

    /// <summary>代理地址脱敏：去掉 user:pass@ 与路径，只留 scheme://host[:port]。</summary>
    public static string MaskProxy(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        var schemeSep = u.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeSep > 0 ? u[..schemeSep] : "";
        var rest = schemeSep > 0 ? u[(schemeSep + 3)..] : u;

        var slash = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = slash >= 0 ? rest[..slash] : rest;
        var at = authority.LastIndexOf('@');
        var host = at >= 0 ? authority[(at + 1)..] : authority;

        return scheme.Length > 0 ? $"{scheme}://{host}" : host;
    }
}
