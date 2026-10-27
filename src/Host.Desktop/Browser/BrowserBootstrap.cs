using HaloAgent.Desktop.Shell;

namespace HaloAgent.Desktop.Browser;

/// <summary>起始 URL 解析：开发态连 Vite，生产态走 app://。生产态绝不返回 file://。</summary>
public static class BrowserBootstrap
{
    public const string ProductionUrl = "app://local/index.html";

    public static string ResolveStartUrl(HostOptions opts) =>
        opts.IsDev ? opts.EffectiveDevUrl : ProductionUrl;

    public static string ResolveStartUrl(bool isDev, string? devUrl) =>
        isDev ? (string.IsNullOrWhiteSpace(devUrl) ? "http://localhost:5173" : devUrl!) : ProductionUrl;
}
