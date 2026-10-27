namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 工具里的路径解析。
///
/// ============ 与权限判定的关系 ============
/// 权限判定（DangerousCommandInspector）只看<b>命令</b>；
/// 文件工具自己走权限（WriteFile/ReadFile），所以这里只做路径规范化，
/// 不做危险判定。但必须做<b>越界检查</b>：
///   - 规范化后确认路径仍然在合理位置
///   - 拒绝含 null 字节的路径
/// </summary>
public static class ToolPaths
{
    /// <summary>简单解析：相对路径转绝对。不检查越界（读操作）。</summary>
    public static string Resolve(string cwd, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return cwd;
        var p = raw.Trim();

        // 展开 ~
        if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            p = p.Length <= 2 ? home : Path.Combine(home, p[2..]);
        }

        var full = Path.IsPathRooted(p) ? p : Path.Combine(cwd, p);
        return Path.GetFullPath(full);
    }

    public sealed record ResolvedPath(string? Path, string? Error);

    /// <summary>
    /// 严格解析（写操作用）：额外拒绝危险路径。
    /// </summary>
    public static ResolvedPath ResolveChecked(ToolContext ctx, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new(null, "path 不能为空");

        if (raw.Contains('\0'))
            return new(null, "path 含非法字符");

        string full;
        try { full = Resolve(ctx.Cwd, raw); }
        catch (Exception ex) { return new(null, $"路径无效: {ex.Message}"); }

        // 拒绝写入裸盘符根与系统目录（与高危命令判定同口径）
        if (full.Length is 2 or 3 && full[1] == ':')
            return new(null, $"拒绝写入磁盘根目录: {full}");

        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(sysRoot) &&
            full.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
            return new(null, $"拒绝写入系统目录: {full}");

        return new(full, null);
    }
}
