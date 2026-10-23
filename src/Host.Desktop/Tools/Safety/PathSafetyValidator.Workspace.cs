using System.Runtime.InteropServices;

namespace Host.Desktop.Tools.Safety;

public static partial class PathSafetyValidator
{
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
