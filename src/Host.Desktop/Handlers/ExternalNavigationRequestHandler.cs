using System.Diagnostics;
using CefSharp;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Handlers;

/// <summary>
/// CEF 主帧导航兜底：任何非应用内地址（非 app://local、非本地 dev server）的
/// 主帧导航一律取消，http(s) 转系统默认浏览器打开，其余仅记日志。
/// 与前端 markdown 链接点击委托（hostBridge.openExternalLink）互为纵深：
/// 前端是主路径，这里保证即使有漏网之鱼也绝不白屏。
/// </summary>
public sealed class ExternalNavigationRequestHandler : CefSharp.Handler.RequestHandler
{
    protected override bool OnBeforeBrowse(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        bool userGesture,
        bool isRedirect)
    {
        try
        {
            // 仅拦截主帧；子帧/resource 不干预
            if (frame == null || !frame.IsMain)
            {
                return false;
            }

            string url = request?.Url ?? string.Empty;
            if (IsInternalUrl(url))
            {
                return false;
            }

            // 外部 http(s)：交系统浏览器
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn($"[NavGuard] 系统浏览器打开失败，已阻止帧内导航：{url}", ex);
                }
            }
            else
            {
                HaloLogger.App.Warn($"[NavGuard] 已阻止 CEF 主帧外部导航：{url}");
            }

            // 返回 true = 取消本次帧内导航，应用页永不离开
            return true;
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[NavGuard] OnBeforeBrowse 异常，已按阻止处理", ex);
            return true;
        }
    }

    private static bool IsInternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return url.StartsWith("app://local", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);
    }
}
