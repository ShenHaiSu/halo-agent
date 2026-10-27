using CefSharp;
using CefSharp.Handler;

namespace HaloAgent.Desktop.Browser;

/// <summary>
/// 渲染进程崩溃恢复。继承 RequestHandler（不直接实现 IRequestHandler，避免 20+ 默认实现）。
/// 注意：CefSharp 109 上 OnRenderProcessTerminated 是 3 参数签名。
/// </summary>
public sealed class CrashRecoveryHandler : RequestHandler
{
    private int _termCount;
    private readonly Action<string> _notify;

    public CrashRecoveryHandler(Action<string> notify) => _notify = notify;

    public int TerminationCount => _termCount;

    protected override void OnRenderProcessTerminated(
        IWebBrowser browserControl, IBrowser browser, CefTerminationStatus status)
    {
        _termCount++;
        AppLog.Error($"渲染进程终止：{status}（第 {_termCount} 次）");

        var shouldReload = status switch
        {
            CefTerminationStatus.AbnormalTermination or CefTerminationStatus.ProcessCrashed => _termCount <= 3,
            CefTerminationStatus.ProcessWasKilled => true,
            _ => false,
        };

        if (status == CefTerminationStatus.OutOfMemory)
        {
            _notify("内存不足，请关闭部分会话后重试。");
            return;
        }

        if (shouldReload)
        {
            _notify("界面进程异常，正在自动恢复…");
            // 延迟一点再 reload，让 CEF 先清理死亡上下文。
            Task.Delay(300).ContinueWith(_ =>
            {
                try { browser.Reload(ignoreCache: false); }
                catch (Exception ex) { AppLog.Error("重载页面失败", ex); }
            });
        }
        else if (_termCount > 3)
        {
            _notify("浏览器内核多次异常，请重启程序。若反复出现，请生成诊断报告并发给技术支持。");
        }
    }
}
