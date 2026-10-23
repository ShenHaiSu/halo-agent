using System.Runtime.InteropServices;

namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 工作区路径校验与系统关键目录写保护。
/// 所有工具传入的相对路径一律基于 WorkspaceRoot 解析；write/edit 严禁触碰系统核心目录。
/// need22-P0 S1：越界一律拒绝（Allowed=false + PATH_OUTSIDE_WORKSPACE），删除“警告放行”；
/// 新增 symlink/junction 解析（GetFinalPathNameByHandle 二次校验，无 API 时降级 GetFullPath）。
/// </summary>
public static partial class PathSafetyValidator
{
    /// <summary>need22-P0 S1 新增错误码（只增不改前端协议）。</summary>
    public static class Codes
    {
        public const string OutsideWorkspace = "PATH_OUTSIDE_WORKSPACE";
        public const string SymlinkFallback = "SYMLINK_RESOLVE_FALLBACK";
    }

    private static readonly string[] ProtectedWriteRoots =
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
    };

    /// <summary>
    /// 基于 WorkspaceRoot 解析输入路径为绝对路径。
    /// 相对路径 → Path.Combine(workspaceRoot, input)；绝对路径 → 原样规范化。
    /// </summary>
    public static string ResolveWorkspacePath(string workspaceRoot, string? inputPath)
    {
        string baseRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return baseRoot;
        }

        string trimmed = inputPath.Trim();
        if (Path.IsPathFullyQualified(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        return Path.GetFullPath(Path.Combine(baseRoot, trimmed));
    }

    /// <summary>
    /// 写保护校验（供 read 场景复用解析，不做拦截）。
    /// </summary>
    public static string ResolveReadPath(string workspaceRoot, string? inputPath) =>
        ResolveWorkspacePath(workspaceRoot, inputPath);

    /// <summary>
    /// need22-P0 S1：GetFullPath → symlink 解析 → 二次越界校验。
    /// 返回 (解析后绝对路径, 是否走了降级通道)。
    /// </summary>
    public static (string Resolved, bool Fallback) NormalizeAndResolve(string fullPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch
        {
            return (fullPath, true);
        }

        string? fin = TryResolveFinalPath(normalized);
        if (fin != null)
        {
            return (fin, false);
        }

        // need22-P0 S1：目标不存在时向上找最近已存在祖先解析（防“区内软链 + 新文件名”穿越；
        // GetFinalPathNameByHandle 仅对已存在路径有效）。
        try
        {
            string? current = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrWhiteSpace(current))
            {
                string? ancestor = TryResolveFinalPath(current);
                if (ancestor != null)
                {
                    string rel;
                    try
                    {
                        rel = Path.GetRelativePath(current, normalized);
                    }
                    catch
                    {
                        break;
                    }
                    if (!rel.StartsWith("..", StringComparison.Ordinal))
                    {
                        return (Path.GetFullPath(Path.Combine(ancestor, rel)), false);
                    }
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            // 祖先 walk 异常则降级
        }

        // 文件不存在或 API 不可用：降级为 GetFullPath（调用方以告警码记录审计）。
        return (normalized, true);
    }

    /// <summary>
    /// 校验写入类操作（write_file / edit_file）的目标路径。
    /// 越界一律 (Allowed=false, PATH_OUTSIDE_WORKSPACE)；无“警告放行”分支。
    /// 返回 (Allowed 是否允许, Error 拒绝原因, FullPath 解析后绝对路径, OutsideWorkspace 是否超出工作区)。
    /// </summary>
    public static (
        bool Allowed,
        string? Error,
        string FullPath,
        bool OutsideWorkspace
    ) ValidateWrite(
        string workspaceRoot,
        string? inputPath,
        IEnumerable<string>? allowedRoots = null
    )
    {
        string fullPath;
        try
        {
            fullPath = ResolveWorkspacePath(workspaceRoot, inputPath);
        }
        catch (Exception ex)
        {
            return (false, $"路径格式非法，无法解析为有效路径：{ex.Message}", inputPath ?? string.Empty, false);
        }

        // 高危系统路径写保护
        if (IsProtectedSystemPath(fullPath))
        {
            return (
                false,
                $"拒绝写入系统关键目录：'{ToDisplayPath(workspaceRoot, fullPath)}'。write_file / edit_file 严禁修改 C:\\Windows、C:\\Program Files、C:\\Program Files (x86) 下的任何文件。",
                fullPath,
                true
            );
        }

        var (resolved, fallback) = NormalizeAndResolve(fullPath);
        EmitSymlinkFallbackAudit(workspaceRoot, fullPath, fallback);
        if (IsOutsideWorkspace(workspaceRoot, resolved, allowedRoots))
        {
            return (
                false,
                $"{Codes.OutsideWorkspace}：拒绝工作区之外的写入：'{ToDisplayPath(workspaceRoot, resolved)}'。请使用工作区内相对路径后重试。",
                resolved,
                true
            );
        }

        return (true, null, resolved, false);
    }

    /// <summary>
    /// need22-P0 S1：校验读取类操作（read_file / list_dir / grep_search / view_image）的目标路径。
    /// 越界一律 (Allowed=false, PATH_OUTSIDE_WORKSPACE)；allowedRoots 为只读豁免根（如 DataRoot/backups）。
    /// </summary>
    public static (
        bool Allowed,
        string? Error,
        string FullPath,
        bool OutsideWorkspace
    ) ValidateRead(
        string workspaceRoot,
        string? inputPath,
        IEnumerable<string>? allowedRoots = null
    )
    {
        string fullPath;
        try
        {
            fullPath = ResolveWorkspacePath(workspaceRoot, inputPath);
        }
        catch (Exception ex)
        {
            return (false, $"路径格式非法，无法解析为有效路径：{ex.Message}", inputPath ?? string.Empty, false);
        }

        var (resolved, fallback) = NormalizeAndResolve(fullPath);
        EmitSymlinkFallbackAudit(workspaceRoot, fullPath, fallback);
        if (IsOutsideWorkspace(workspaceRoot, resolved, allowedRoots))
        {
            return (
                false,
                $"{Codes.OutsideWorkspace}：拒绝工作区之外的读取：'{ToDisplayPath(workspaceRoot, resolved)}'。请使用工作区内相对路径后重试。",
                resolved,
                true
            );
        }

        return (true, null, resolved, false);
    }

    /// <summary>
    /// 是否命中系统关键目录（自身或其子路径），大小写不敏感、按分隔符边界匹配。
    /// </summary>
    public static bool IsProtectedSystemPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string normalized = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        foreach (string root in ProtectedWriteRoots)
        {
            if (
                string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
                || normalized.StartsWith(
                    root + Path.AltDirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        // 兼容当前系统实际的 Windows / ProgramFiles 目录（大小写、异盘符场景）
        string[] dynamicRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (string root in dynamicRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normRoot = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            if (
                string.Equals(normalized, normRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(
                    normRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}
