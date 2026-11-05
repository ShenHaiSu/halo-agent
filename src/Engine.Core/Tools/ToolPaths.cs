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
     /// <summary>
     /// 三态存在性判定（给 read_file / edit 共用）：
     /// 目录 → 提示改用 list_directory；文件存在 → null（无问题）；都不存在 →「文件不存在」。
     /// 避免把「传了目录」误报成「文件不存在」，模型会用错工具重试。
     /// </summary>
     public static string? DescribeMissing(string full)
     {
         if (Directory.Exists(full))
             return $"这是一个目录，不是文件: {full}。查看目录请用 list_directory";
         if (File.Exists(full)) return null;
         return $"文件不存在: {full}";
     }
 
     /// <summary>
     /// 取真实路径用于「同文件」判定的 key。
     ///
     /// 为什么要 realpath：符号链接 / 硬链接 / 相对路径 / 大小写差异
     /// 会让同一个物理文件有多个字符串表示；只用 GetFullPath 会把它们当成不同文件，
     /// 串行化失效 → 并行写同一文件时仍会丢数据。
     ///
     /// 文件还不存在（新建）时无法解析链接 → 退化为 GetFullPath。
     /// </summary>
     public static string ResolveRealistic(string fullPath)
     {
         try
         {
             var fi = new FileInfo(fullPath);
             var target = fi.Exists ? fi.ResolveLinkTarget(returnFinalTarget: true) : null;
             var resolved = target?.FullName ?? fullPath;
             return Path.GetFullPath(resolved);
         }
         catch
         {
             return Path.GetFullPath(fullPath);
         }
     }
}
