using CefSharp;

namespace HaloAgent.Desktop.Handlers;

/// <summary>
/// 浏览器生命周期。
///   - OnBeforeClose：用户用 DevTools 或快捷键关页面时，不要真的关（否则窗口变白板）。
///   - OnBeforePopup：所有 target=_blank 都改成"用系统浏览器打开"，避免在应用内弹无边框小窗。
/// </summary>
public sealed class HostLifeSpanHandler : ILifeSpanHandler
{
    private readonly Func<bool> _allowClose;
    public HostLifeSpanHandler(Func<bool> allowClose) => _allowClose = allowClose;

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
        out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        if (targetDisposition == WindowOpenDisposition.CurrentTab)
        {
            // 当前标签内跳转：允许
            return false;
        }
        // 新窗口 → 交给系统浏览器
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(targetUrl) { UseShellExecute = true });
        }
        catch { }
        return true; // 阻止 CEF 建新窗口
    }

    public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        // 窗口正在关闭时放行
        if (_allowClose()) return false;
        return true; // 拦住
    }

    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
}
