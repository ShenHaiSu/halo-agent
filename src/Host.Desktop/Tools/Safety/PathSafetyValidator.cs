using System.Runtime.InteropServices;

namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 工作区路径校验与系统关键目录写保护。
/// 所有工具传入的相对路径一律基于 WorkspaceRoot 解析；write/edit 严禁触碰系统核心目录。
/// need22-P0 S1：越界一律拒绝（Allowed=false + PATH_OUTSIDE_WORKSPACE），删除“警告放行”；
/// 新增 symlink/junction 解析（GetFinalPathNameByHandle 二次校验，无 API 时降级 GetFullPath）。
/// </summary>
public static class PathSafetyValidator
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

    /// <summary>
    /// 判断绝对路径是否落在工作区之外（allowedRoots 豁免，大小写不敏感、按分隔符边界匹配）。
    /// </summary>
    public static bool IsOutsideWorkspace(
        string workspaceRoot,
        string fullPath,
        IEnumerable<string>? allowedRoots = null
    )
    {
        try
        {
            string baseRoot =
                Path.GetFullPath(
                        string.IsNullOrWhiteSpace(workspaceRoot)
                            ? Directory.GetCurrentDirectory()
                            : workspaceRoot
                    )
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string normalized = Path.GetFullPath(fullPath);
            if (
                string.Equals(
                    normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    baseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            if (normalized.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (allowedRoots != null)
            {
                foreach (string extra in allowedRoots)
                {
                    if (string.IsNullOrWhiteSpace(extra))
                    {
                        continue;
                    }

                    string allowRoot;
                    try
                    {
                        allowRoot =
                            Path.GetFullPath(extra)
                                .TrimEnd(
                                    Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar
                                ) + Path.DirectorySeparatorChar;
                    }
                    catch
                    {
                        continue;
                    }

                    if (
                        string.Equals(
                            normalized.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar
                            ),
                            allowRoot.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar
                            ),
                            StringComparison.OrdinalIgnoreCase
                        ) || normalized.StartsWith(allowRoot, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>need15 P2-2：校验 backup_path 位于 &lt;DataRoot&gt;/backups/ 下，防任意读。</summary>
    public static (bool Allowed, string? Error, string FullPath) ValidateBackupRead(
        string dataRoot,
        string? inputPath
    )
    {
        string root = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.GetTempPath()
            : Path.GetFullPath(dataRoot);
        string backupsRoot =
            Path.GetFullPath(Path.Combine(root, "backups")) + Path.DirectorySeparatorChar;
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return (false, "缺少必需参数 'backup_path'。", string.Empty);
            }

            full = Path.GetFullPath(inputPath);
        }
        catch (Exception ex)
        {
            return (false, $"路径格式非法，无法解析为有效路径：{ex.Message}", inputPath ?? string.Empty);
        }

        var (resolved, _) = NormalizeAndResolve(full);
        if (!resolved.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (
                false,
                $"拒绝读取备份目录之外的路径：'{resolved}'。backup_path 必须位于 <DataRoot>/backups/ 下。",
                resolved
            );
        }

        if (!File.Exists(resolved))
        {
            return (false, $"备份文件不存在：'{resolved}'。", resolved);
        }

        return (true, null, resolved);
    }

    /// <summary>
    /// need22-P0 S5：错误回显去绝对路径（工作区内显示相对路径，区外显示叶名 + 父目录提示）。
    /// </summary>
    public static string ToDisplayPath(string workspaceRoot, string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(
                Path.GetFullPath(
                    string.IsNullOrWhiteSpace(workspaceRoot)
                        ? Directory.GetCurrentDirectory()
                        : workspaceRoot
                ),
                Path.GetFullPath(fullPath)
            );
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                return relative;
            }
        }
        catch
        {
            // 降级：叶名
        }

        try
        {
            return Path.GetFileName(fullPath);
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>
    /// 计算相对工作区的显示路径（超出工作区则返回绝对路径）。
    /// </summary>
    public static string GetWorkspaceRelativePath(string workspaceRoot, string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(
                Path.GetFullPath(workspaceRoot),
                Path.GetFullPath(fullPath)
            );
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                return Path.GetFullPath(fullPath);
            }

            return relative;
        }
        catch (Exception)
        {
            return fullPath;
        }
    }

    // ---------------- symlink / junction 解析 ----------------

    /// <summary>
    /// need22-P0 S1：symlink 解析降级审计（已存在路径仍走 GetFullPath 时记 SYMLINK_RESOLVE_FALLBACK 告警码）。
    /// 不存在路径的降级属预期行为，不打扰。
    /// </summary>
    private static void EmitSymlinkFallbackAudit(
        string workspaceRoot,
        string fullPath,
        bool fallback
    )
    {
        if (!fallback)
        {
            return;
        }

        try
        {
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return;
            }

            Infrastructure.Logging.HaloLogger.App.Warn(
                $"[PathSafety] {Codes.SymlinkFallback}：'{ToDisplayPath(workspaceRoot, fullPath)}' symlink 解析降级为 GetFullPath（API 不可用），已按字面路径校验。"
            );
        }
        catch
        {
            // 审计失败不影响校验主路径
        }
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 用 GetFinalPathNameByHandle 解析 symlink/junction 到最终路径。
    /// 文件不存在 / 非 Windows / API 不可用时返回 null（调用方降级 GetFullPath）。
    /// net6.0-windows 红线：仅 Win32 P/Invoke（Vista+，Win7 可用），无新 API 依赖。
    /// </summary>
    public static string? TryResolveFinalPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        bool isDir = Directory.Exists(fullPath);
        if (!isDir && !File.Exists(fullPath))
        {
            return null;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateFileW(
                fullPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero
            );
            if (handle == IntPtr.Zero || handle.ToInt64() == -1)
            {
                return null;
            }

            char[] buf = new char[32768];
            uint len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, 0);
            if (len == 0 || len >= buf.Length)
            {
                return null;
            }

            string final = new string(buf, 0, (int)len);
            // 去 \\?\ / \\?\UNC\ 前缀
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                final = @"\\" + final.Substring(@"\\?\UNC\".Length);
            }
            else if (final.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                final = final.Substring(@"\\?\".Length);
            }

            return Path.GetFullPath(final);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                {
                    CloseHandle(handle);
                }
            }
            catch
            {
                // 忽略句柄关闭异常
            }
        }
    }
}
