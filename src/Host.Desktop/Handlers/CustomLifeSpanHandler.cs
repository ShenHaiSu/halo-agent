using System.Diagnostics;
using CefSharp;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Handlers;

/// <summary>
/// CEF 生命周期与弹出窗口拦截处理器：
/// 拦截 target="_blank" 或 window.open 行为，调用系统默认浏览器打开外部链接，防止弹出空白原生 WinForms 窗口。
/// </summary>
public sealed class CustomLifeSpanHandler : ILifeSpanHandler
{
    public bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        string targetFrameName,
        WindowOpenDisposition targetDisposition,
        bool userGesture,
        IPopupFeatures popupFeatures,
        IWindowInfo windowInfo,
        IBrowserSettings browserSettings,
        ref bool noJavascriptAccess,
        out IWebBrowser? newBrowser)
    {
        newBrowser = null;

        if (!string.IsNullOrWhiteSpace(targetUrl) &&
            (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"Failed to open external URL in system browser: {targetUrl}", ex);
            }
        }

        // 返回 true 阻止 CEF 创建原生弹窗
        return true;
    }

    public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        return false;
    }

    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }
}
