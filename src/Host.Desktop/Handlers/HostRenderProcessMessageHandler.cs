using CefSharp;

namespace HaloAgent.Desktop.Handlers;

/// <summary>
/// 渲染进程崩溃/无响应处理。
/// Win7 上，显卡驱动问题常表现为渲染进程直接退出——界面变成灰块。
/// 这里只记录 OnUncaughtException；崩溃自愈在 P3 完善。
/// </summary>
public sealed class HostRenderProcessMessageHandler : IRenderProcessMessageHandler
{
    public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

    public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

    public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node) { }

    public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception)
    {
        // 注意：CefSharp 109 的 JavascriptException 不是 System.Exception（纯数据类），
        // 只能记录 Message，不能当 Exception 传。
        AppLog.Error($"页面 JS 未捕获异常: {exception.Message} @ {frame.Url}");
    }
}
