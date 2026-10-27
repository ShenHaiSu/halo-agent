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
/// </summary>
public static class HttpClientFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static HttpClient Create(string? proxyUrl, int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            // 直连（或跟随系统环境变量由调用方决定；这里默认为"不显式设代理"）
            handler.UseProxy = false;
        }
        else
        {
            var proxy = new System.Net.WebProxy(proxyUrl);
            // 尊重 NO_PROXY 环境变量
            var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                          ?? Environment.GetEnvironmentVariable("no_proxy");
            if (!string.IsNullOrWhiteSpace(noProxy))
            {
                foreach (var host in noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    proxy.BypassList = (proxy.BypassList ?? Array.Empty<string>())
                        .Append("^" + System.Text.RegularExpressions.Regex.Escape(host.Replace("*", "")) + ".*")
                        .ToArray();
            }
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        return client;
    }

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
