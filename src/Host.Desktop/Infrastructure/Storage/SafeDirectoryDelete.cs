using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Host.Desktop.Services;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 破坏性删除的唯一路径边界：只允许删除“已验证父目录下的一层命名子目录”，
/// 删除前复检边界与 reparse point，不存在视为幂等成功。
/// </summary>
internal static class SafeDirectoryDelete
{
    internal static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    internal static bool ContainsReparsePoint(string directory, CancellationToken ct) =>
        SessionDirectoryDeletion.ContainsReparsePoint(directory, ct);

    /// <summary>
    /// 删除 parent/childName。recursive=true 仅用于 Purge 的两个明确根；
    /// 任一 reparse point、边界复检失败或 IO 异常都记录失败路径，不抛出。
    /// </summary>
    internal static bool TryDeleteChild(
        string parent,
        string childName,
        bool recursive,
        CancellationToken ct,
        out IReadOnlyList<string> failedPaths
    )
    {
        failedPaths = Array.Empty<string>();
        if (
            !SessionDirectoryDeletion.TryResolveChildDirectory(parent, childName, out string target)
        )
        {
            failedPaths = new[] { $"{parent}{Path.DirectorySeparatorChar}{childName}" };
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(target))
            {
                // 同名普通文件或链接不是我们允许的目标，保守失败。
                if (File.Exists(target))
                {
                    failedPaths = new[] { target };
                    return false;
                }
                return true;
            }

            // 删除前再次规范化，防止检查与删除之间被替换为 junction。
            if (
                !SessionDirectoryDeletion.TryResolveChildDirectory(
                    parent,
                    childName,
                    out string recheck
                ) || !string.Equals(recheck, target, StringComparison.OrdinalIgnoreCase)
            )
            {
                failedPaths = new[] { target };
                return false;
            }

            if (IsReparsePoint(target) || (recursive && ContainsReparsePoint(target, ct)))
            {
                failedPaths = new[] { target };
                return false;
            }

            Directory.Delete(target, recursive);
            if (Directory.Exists(target))
            {
                failedPaths = new[] { target };
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            failedPaths = new[] { target };
            return false;
        }
    }

    /// <summary>
    /// 只删除已知的空结构：两个占位索引 + 两个空目录 + 域根（全部非递归）。
    /// 检查后若有任何新内容插入，非递归删除会失败，不会误删新内容。
    /// </summary>
    internal static bool TryDeleteEmptyProjectDomain(
        string workspaceRoot,
        CancellationToken ct,
        out IReadOnlyList<string> failedPaths
    )
    {
        var failures = new List<string>();
        failedPaths = failures;
        string haloRoot;
        try
        {
            haloRoot = SkillMemoryService.ResolveProjectRoot(workspaceRoot);
        }
        catch (Exception)
        {
            failures.Add(workspaceRoot);
            return false;
        }

        foreach (string kind in new[] { "skills", "memory" })
        {
            ct.ThrowIfCancellationRequested();
            string domainRoot = Path.Combine(haloRoot, kind);
            string indexPath = Path.Combine(domainRoot, "_index.json");
            try
            {
                if (File.Exists(indexPath))
                    File.Delete(indexPath);
            }
            catch (Exception)
            {
                failures.Add(indexPath);
            }
            try
            {
                if (Directory.Exists(domainRoot))
                    Directory.Delete(domainRoot, recursive: false);
            }
            catch (Exception)
            {
                failures.Add(domainRoot);
            }
        }

        try
        {
            if (Directory.Exists(haloRoot))
                Directory.Delete(haloRoot, recursive: false);
        }
        catch (Exception)
        {
            failures.Add(haloRoot);
        }
        return failures.Count == 0;
    }
}
