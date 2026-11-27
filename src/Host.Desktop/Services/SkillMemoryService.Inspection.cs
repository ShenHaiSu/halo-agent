#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed partial class SkillMemoryService
{
    /// <summary>
    /// 保守物理检查项目域，不信任索引条目数：只有 skills/memory 两个目录且
    /// 除 _index.json 外没有任何条目时才会判定为空域。任何未知内容、异常形态、
    /// reparse point 或读取失败都保留，避免“索引损坏导致误删”。
    /// </summary>
    public static ProjectDomainInspection InspectProjectDomain(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return NotSafe(ProjectDomainReasons.PathTraversal);

        string haloRoot;
        try
        {
            haloRoot = ResolveProjectRoot(workspaceRoot);
        }
        catch (Exception)
        {
            return NotSafe(ProjectDomainReasons.PathTraversal);
        }

        try
        {
            if (!Directory.Exists(haloRoot))
                return new ProjectDomainInspection(
                    false,
                    true,
                    false,
                    false,
                    false,
                    ProjectDomainReasons.NotFound
                );

            bool hasValidEntries = false;
            bool hasUnknownContent = false;
            bool hasReparsePoint = SafeDirectoryDelete.IsReparsePoint(haloRoot);
            var allowedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "skills",
                "memory"
            };

            foreach (string entry in Directory.EnumerateFileSystemEntries(haloRoot))
            {
                string name = Path.GetFileName(entry);
                if (SafeDirectoryDelete.IsReparsePoint(entry))
                    hasReparsePoint = true;
                if (!Directory.Exists(entry) || !allowedKinds.Contains(name))
                {
                    hasUnknownContent = true;
                    continue;
                }

                foreach (string child in Directory.EnumerateFileSystemEntries(entry))
                {
                    if (SafeDirectoryDelete.IsReparsePoint(child))
                        hasReparsePoint = true;
                    string childName = Path.GetFileName(child);
                    if (Directory.Exists(child))
                    {
                        // 保守判定：任何条目目录都视为实质内容；索引损坏也不会漏网。
                        hasValidEntries = true;
                        if (SafeDirectoryDelete.ContainsReparsePoint(child, default))
                            hasReparsePoint = true;
                    }
                    else if (
                        !string.Equals(childName, "_index.json", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        hasUnknownContent = true;
                    }
                }
            }

            string? reason = null;
            if (hasReparsePoint)
                reason = ProjectDomainReasons.ReparsePoint;
            else if (hasUnknownContent)
                reason = ProjectDomainReasons.UnknownContent;
            else if (hasValidEntries)
                reason = ProjectDomainReasons.ValidEntries;
            else
                reason = ProjectDomainReasons.Empty;

            bool safeEmpty = !hasReparsePoint && !hasUnknownContent && !hasValidEntries;
            return new ProjectDomainInspection(
                true,
                safeEmpty,
                hasValidEntries,
                hasUnknownContent,
                hasReparsePoint,
                reason
            );
        }
        catch (Exception)
        {
            return NotSafe(ProjectDomainReasons.InspectionFailed);
        }
    }

    private static ProjectDomainInspection NotSafe(string reason) =>
        new(false, false, false, false, false, reason);
}
