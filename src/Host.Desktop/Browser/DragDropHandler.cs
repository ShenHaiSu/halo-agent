using System.Windows.Forms;
using CefSharp;
using CefSharp.Enums;


namespace HaloAgent.Desktop.Browser;

/// <summary>
/// 文件拖放（CEF IDragHandler）。WinForms AllowDrop 在浏览器区域无效（CEF 吃掉事件），
/// 必须用 CEF 的 IDragHandler。多文件受 109 的 IDragData 限制（单 FileName）。
/// </summary>
public sealed class DragDropHandler : IDragHandler
{
    private readonly Action<string[]> _onFilesDropped;
    private string[] _pending = Array.Empty<string>();

    public DragDropHandler(Action<string[]> onFilesDropped) => _onFilesDropped = onFilesDropped;

    public bool OnDragEnter(IWebBrowser browserControl, IBrowser browser, IDragData dragData, DragOperationsMask mask)
    {
        _pending = ExtractFilePaths(dragData);
        return _pending.Length > 0;
    }

    public void OnDraggableRegionsChanged(IWebBrowser browserControl, IBrowser browser, IFrame frame, IList<DraggableRegion> regions)
    {
        var ctl = browserControl as Control;
        var form = ctl?.FindForm();
        if (form is Shell.ChromeForm cf)
            cf.SetWebDraggableRegions(regions);
    }

    public void OnDragLeave(IWebBrowser browserControl, IBrowser browser, IDragData dragData) { }

    public bool OnDropTarget(IWebBrowser browserControl, IBrowser browser, IDragData dragData, DragOperationsMask mask)
    {
        var files = _pending.Length > 0 ? _pending : ExtractFilePaths(dragData);
        _pending = Array.Empty<string>();
        if (files.Length == 0) return false;
        _onFilesDropped(files);
        return true;
    }

    private static string[] ExtractFilePaths(IDragData data)
    {
        // CefSharp 109 的 IDragData 只暴露单个 FileName。
        if (!data.IsFile) return Array.Empty<string>();
        var name = data.FileName;
        return string.IsNullOrEmpty(name) ? Array.Empty<string>() : new[] { name };
    }
}
