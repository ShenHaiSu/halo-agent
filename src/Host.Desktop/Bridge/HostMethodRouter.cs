namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 宿主自己实现的方法（不转发引擎）。
/// fs.browse/pickFiles/pickSavePath 需原生对话框（引擎是无窗口进程，弹框会成幽灵窗口），
/// fs.readImageAsBase64 供拖入图片预览。
/// 本集合必须与 agent.schema.json 中 implementedIn="host" 的集合完全一致（契约测试校验）。
/// </summary>
public static class HostMethodRouter
{
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal)
    {
        "fs.browse",
        "fs.pickFiles",
        "fs.pickSavePath",
        "fs.readImageAsBase64",
    };

    public static bool IsHostMethod(string method) => Methods.Contains(method);
}
