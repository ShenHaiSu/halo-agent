namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 工作区路径校验与系统关键目录写保护。
/// 所有工具传入的相对路径一律基于 WorkspaceRoot 解析；write/edit 严禁触碰系统核心目录。
/// </summary>
public static class PathSafetyValidator
{
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
    /// 校验写入类操作（write_file / edit_file）的目标路径。
    /// 返回 (Allowed 是否允许, Error 拒绝原因, FullPath 解析后绝对路径, OutsideWorkspace 是否超出工作区)。
    /// </summary>
    public static (bool Allowed, string? Error, string FullPath, bool OutsideWorkspace) ValidateWrite(
        string workspaceRoot,
        string? inputPath)
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
            return (false,
                $"拒绝写入系统关键目录：'{fullPath}'。write_file / edit_file 严禁修改 C:\\Windows、C:\\Program Files、C:\\Program Files (x86) 下的任何文件。",
                fullPath,
                true);
        }

        bool outside = IsOutsideWorkspace(workspaceRoot, fullPath);
        return (true, null, fullPath, outside);
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

        string normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (string root in ProtectedWriteRoots)
        {
            if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

            string normRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, normRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断绝对路径是否落在工作区之外。
    /// </summary>
    public static bool IsOutsideWorkspace(string workspaceRoot, string fullPath)
    {
        try
        {
            string baseRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot)
                ? Directory.GetCurrentDirectory()
                : workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string normalized = Path.GetFullPath(fullPath);
            if (string.Equals(
                    normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    baseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !normalized.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }
     /// <summary>need15 P2-2：校验 backup_path 位于 &lt;DataRoot&gt;/backups/ 下，防任意读。</summary>
     public static (bool Allowed, string? Error, string FullPath) ValidateBackupRead(string dataRoot, string? inputPath)
     {
         string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : Path.GetFullPath(dataRoot);
         string backupsRoot = Path.GetFullPath(Path.Combine(root, "backups")) + Path.DirectorySeparatorChar;
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

         if (!full.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase))
         {
             return (false, $"拒绝读取备份目录之外的路径：'{full}'。backup_path 必须位于 <DataRoot>/backups/ 下。", full);
         }

         if (!File.Exists(full))
         {
             return (false, $"备份文件不存在：'{full}'。", full);
         }

         return (true, null, full);
     }
    /// <summary>
    /// 计算相对工作区的显示路径（超出工作区则返回绝对路径）。
    /// </summary>
    public static string GetWorkspaceRelativePath(string workspaceRoot, string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(
                Path.GetFullPath(workspaceRoot), Path.GetFullPath(fullPath));
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
}
