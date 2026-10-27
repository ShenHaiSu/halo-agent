using System.Text.Json;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 原生文件对话框（全部 UI 线程弹框；用户取消返回 null 而不是异常）。
/// 由 AgentBridge 路由 fs.* 到这里。
/// </summary>
public sealed class HostFileDialogs
{
    private readonly Func<Form> _formAccessor;

    public HostFileDialogs(Func<Form> formAccessor) => _formAccessor = formAccessor;

    public async Task<object?> RouteAsync(string method, object? prms)
    {
        var el = prms is null ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(prms, BridgeJson.Options);
        string? Get(string name)
        {
            if (el is null || el.Value.ValueKind != JsonValueKind.Object) return null;
            return el.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        bool GetBool(string name)
        {
            if (el is null || el.Value.ValueKind != JsonValueKind.Object) return false;
            return el.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }

        return method switch
        {
            "fs.browse" => await BrowseAsync(Get("initialDir"), Get("title")).ConfigureAwait(false),
            "fs.pickFiles" => await PickFilesAsync(Get("initialDir"), Get("filter"), GetBool("multiple")).ConfigureAwait(false),
            "fs.pickSavePath" => await PickSavePathAsync(Get("initialDir"), Get("defaultName"), Get("filter")).ConfigureAwait(false),
            "fs.readImageAsBase64" => await ReadImageAsBase64Async(Get("path") ?? "").ConfigureAwait(false),
            _ => throw new ArgumentException($"宿主不支持方法：{method}"),
        };
    }

    public Task<string?> BrowseAsync(string? initialDir, string? title)
    {
        return OnUiThread(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = title ?? "选择文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                dlg.SelectedPath = initialDir;
            return dlg.ShowDialog(_formAccessor()) == DialogResult.OK ? dlg.SelectedPath : null;
        });
    }

    public Task<string[]?> PickFilesAsync(string? initialDir, string? filter, bool multiple)
    {
        return OnUiThread(() =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择文件",
                Filter = string.IsNullOrWhiteSpace(filter) ? "所有文件 (*.*)|*.*" : filter,
                Multiselect = multiple,
                CheckFileExists = true,
                CheckPathExists = true,
            };
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog(_formAccessor()) == DialogResult.OK ? dlg.FileNames : null;
        });
    }

    public Task<string?> PickSavePathAsync(string? initialDir, string? defaultName, string? filter)
    {
        return OnUiThread(() =>
        {
            using var dlg = new SaveFileDialog
            {
                Title = "保存到",
                Filter = string.IsNullOrWhiteSpace(filter) ? "所有文件 (*.*)|*.*" : filter,
                FileName = defaultName ?? "",
                OverwritePrompt = true,
            };
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog(_formAccessor()) == DialogResult.OK ? dlg.FileName : null;
        });
    }

    public async Task<string?> ReadImageAsBase64Async(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("文件不存在", full);
        const long MaxBytes = 20 * 1024 * 1024;
        var len = new FileInfo(full).Length;
        if (len > MaxBytes)
            throw new InvalidOperationException($"图片过大（{len / 1024 / 1024} MB），上限 20 MB");
        var bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
        return Convert.ToBase64String(bytes);
    }

    private Task<T> OnUiThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var form = _formAccessor();
        if (form.IsDisposed)
        {
            tcs.TrySetException(new ObjectDisposedException("MainForm"));
            return tcs.Task;
        }
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
        }
        catch (InvalidOperationException ex)
        {
            tcs.TrySetException(new InvalidOperationException("窗口不可用", ex));
        }
        return tcs.Task;
    }
}
