using CefSharp;

namespace HaloAgent.Desktop.Handlers;

/// <summary>
/// 下载处理。P0 阶段：把所有下载取消并交给系统浏览器。
/// 理由：应用内的下载需要一整套 UI（进度、路径选择、冲突处理），P0 不做。
/// </summary>
public sealed class HostDownloadHandler : IDownloadHandler
{
    public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        => false;

    // CefSharp 109 的接口签名（老版本用的是未标记 obsolete 的重载）
    public void OnBeforeDownload(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        callback.Dispose();
    }

    public void OnDownloadUpdated(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IDownloadItemCallback callback) { }
}
