using CefSharp;

namespace Host.Desktop.Handlers;

/// <summary>
/// CEF 右键上下文菜单安全处理器：
/// 生产环境下屏蔽“打印”、“查看源代码”、“另存为”等无用项，仅在输入框保留编辑操作，开发态保留“检查元素（Inspect）”。
/// </summary>
public sealed class CustomMenuHandler : IContextMenuHandler
{
    private const CefMenuCommand ShowDevToolsCommandId = (CefMenuCommand)26501;
    private readonly bool _isDevelopment;

    public CustomMenuHandler(bool isDevelopment = false)
    {
        _isDevelopment = isDevelopment;
    }

    public void OnBeforeContextMenu(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IContextMenuParams parameters,
        IMenuModel model)
    {
        model.Clear();

        if (parameters.IsEditable)
        {
            model.AddItem(CefMenuCommand.Undo, "撤销(&U)");
            model.AddItem(CefMenuCommand.Redo, "重做(&R)");
            model.AddSeparator();
            model.AddItem(CefMenuCommand.Cut, "剪切(&T)");
            model.AddItem(CefMenuCommand.Copy, "复制(&C)");
            model.AddItem(CefMenuCommand.Paste, "粘贴(&P)");
            model.AddItem(CefMenuCommand.SelectAll, "全选(&A)");
        }
        else if (!string.IsNullOrWhiteSpace(parameters.SelectionText))
        {
            model.AddItem(CefMenuCommand.Copy, "复制(&C)");
        }

        if (_isDevelopment)
        {
            if (model.Count > 0)
            {
                model.AddSeparator();
            }

            model.AddItem(ShowDevToolsCommandId, "检查元素 (Inspect DevTools)");
        }
    }

    public bool OnContextMenuCommand(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IContextMenuParams parameters,
        CefMenuCommand commandId,
        CefEventFlags eventFlags)
    {
        if (commandId == ShowDevToolsCommandId)
        {
            browser.GetHost()?.ShowDevTools(null, parameters.XCoord, parameters.YCoord);
            return true;
        }

        return false;
    }

    public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
    {
    }

    public bool RunContextMenu(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IContextMenuParams parameters,
        IMenuModel model,
        IRunContextMenuCallback callback)
    {
        return false;
    }
}
