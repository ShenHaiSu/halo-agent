namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 会话输出目录只读豁免根计算（写校验永不调用本类）。
/// 落盘与读校验同调一函数，保证目录口径一致。
/// </summary>
internal static class SessionOutputRoots
{
    /// <summary>
    /// 拼出会话输出目录全路径（落盘与豁免同源）。
    /// 空数据根回退系统临时目录，与落盘历史行为一致。
    /// </summary>
    internal static string BuildSessionToolOutputsDir(
        string? dataRoot,
        string? workspaceId,
        string? sessionId
    )
    {
        string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
        string ws = (workspaceId ?? string.Empty).Trim();
        string sess = (sessionId ?? string.Empty).Trim();
        return Path.Combine(root, "sessions", ws, sess, "tool-outputs");
    }

    /// <summary>
    /// 当前会话输出目录只读豁免根（写校验永不传入）。
    /// 仅返回本会话 tool-outputs 单目录；标识异常时返回空数组（默认拒绝，不抛）。
    /// </summary>
    internal static string[] GetSessionReadAllowedRoots(Tools.ToolContext context)
    {
        string ws = (context.WorkspaceId ?? string.Empty).Trim();
        string sess = (context.SessionId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(ws) || string.IsNullOrEmpty(sess))
        {
            return Array.Empty<string>();
        }

        if (
            ws.Contains("..", StringComparison.Ordinal)
            || sess.Contains("..", StringComparison.Ordinal)
            || ws.IndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' }
            ) >= 0
            || sess.IndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' }
            ) >= 0
        )
        {
            return Array.Empty<string>();
        }

        string root = string.IsNullOrWhiteSpace(context.DataRoot)
            ? Path.GetTempPath()
            : context.DataRoot;
        string dir;
        try
        {
            dir = Path.GetFullPath(Path.Combine(root, "sessions", ws, sess, "tool-outputs"));
        }
        catch
        {
            return Array.Empty<string>();
        }

        return new[] { dir };
    }

    /// <summary>
    /// 会话输出单文件形态校验（落盘唯一形态）。
    /// 目录扫描不调用本函数。
    /// </summary>
    internal static bool IsSessionOutputFileName(string? fullPath)
    {
        string name;
        try
        {
            name = Path.GetFileName(fullPath ?? string.Empty);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.StartsWith("call_", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase);
    }
}
