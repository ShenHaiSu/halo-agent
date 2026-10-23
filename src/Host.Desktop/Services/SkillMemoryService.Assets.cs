#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed partial class SkillMemoryService
{
    /// <summary>写入资产文件（单文件 512KB 上限；含逃逸校验）。返回落盘绝对路径。</summary>
    public string WriteAsset(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string assetRelativePath,
        byte[] content,
        string? workspaceRoot = null
    )
    {
        string normalized = NormalizeAndValidateName(name);
        if (content != null && content.Length > MaxAssetBytes)
            throw new SkillMemoryValidationException(
                "SKILL_VALIDATION",
                $"资产 {content.Length} 字节超限（上限 {MaxAssetBytes} 字节）。"
            );
        // 复用逃逸校验（要求条目已存在）。
        string abs = ResolveAssetPath(scope, kind, normalized, assetRelativePath, workspaceRoot);
        lock (_syncLock)
        {
            string? dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            WriteBytesAtomic(abs, content ?? Array.Empty<byte>());
            RebuildIndexLocked(scope, kind, workspaceRoot);
            InvalidateMergedCacheLocked();
            return abs;
        }
    }

    /// <summary>删除整条（先留 .deleted-&lt;ts&gt;.bak 一份同级备份，再删原目录 + 重建索引）。</summary>
    public void Delete(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string? workspaceRoot = null
    )
    {
        string normalized = NormalizeAndValidateName(name);
        lock (_syncLock)
        {
            string? domainRoot = ResolveDomainRoot(scope, kind, workspaceRoot);
            if (domainRoot == null || !Directory.Exists(domainRoot))
                throw new SkillMemoryValidationException("SKILL_NOT_FOUND", $"条目不存在：{normalized}。");
            string? dir = FindEntryDir(domainRoot, normalized);
            if (dir == null)
                throw new SkillMemoryValidationException("SKILL_NOT_FOUND", $"条目不存在：{normalized}。");

            // 同级仅保留 1 份 bak：先清旧 bak 再拷新 bak。
            foreach (
                var old in Directory.EnumerateFileSystemEntries(
                    domainRoot,
                    normalized + ".deleted-*.bak"
                )
            )
            {
                try
                {
                    if (Directory.Exists(old))
                        Directory.Delete(old, recursive: true);
                    else
                        File.Delete(old);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn($"[SkillMemory] 旧 bak 清理失败（继续删除）：{old}", ex);
                }
            }
            string bak = Path.Combine(
                domainRoot,
                $"{normalized}.deleted-{DateTime.UtcNow:yyyyMMddHHmmss}.bak"
            );
            try
            {
                CopyDirectory(dir, bak);
                HaloLogger.App.Info($"[SkillMemory] 已删除 '{normalized}'，备份保留于：{bak}");
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"[SkillMemory] 删除前备份失败（继续删除）：{normalized}", ex);
            }
            Directory.Delete(dir, recursive: true);

            RebuildIndexLocked(scope, kind, workspaceRoot);
            InvalidateMergedCacheLocked();
        }
    }

    /// <summary>启用/禁用开关（保留正文与元数据，仅改 enabled + 索引重建）。</summary>
    public void SetEnabled(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string? workspaceRoot,
        bool enabled
    )
    {
        SkillMemoryEntry? current = Get(scope, kind, name, workspaceRoot);
        if (current == null)
            throw new SkillMemoryValidationException("SKILL_NOT_FOUND", $"条目不存在：{name?.Trim()}。");
        CreateOrUpdate(
            scope,
            kind,
            current.Name,
            current.BodyMarkdown,
            workspaceRoot,
            enabled: enabled,
            priority: current.Priority,
            version: current.Version,
            pin: current.Pin,
            title: current.Title,
            summary: current.Summary
        );
    }

    /// <summary>重建单域单类 _index.json（损坏/版本不符时调用；原子写）。</summary>
    public void RebuildIndex(SkillMemoryScope scope, SkillMemoryKind kind, string? workspaceRoot)
    {
        lock (_syncLock)
        {
            RebuildIndexLocked(scope, kind, workspaceRoot);
            InvalidateMergedCacheLocked();
        }
    }

    // ---------- 命名校验 ----------

    /// <summary>大小写归一（转小写）+ 正则 + 保留名拒绝。违规抛 SKILL_VALIDATION。</summary>
    public static string NormalizeAndValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new SkillMemoryValidationException("SKILL_VALIDATION", "名称不能为空。");
        string n = name.Trim().ToLowerInvariant();
        if (n.Length < 2 || n.Length > 48 || !NameRegex.IsMatch(n))
            throw new SkillMemoryValidationException(
                "SKILL_VALIDATION",
                $"非法名称 '{name.Trim()}'：须 2–48 字符、小写字母数字开头、仅含小写字母/数字/-/_。"
            );
        if (ReservedNames.Contains(n))
            throw new SkillMemoryValidationException("SKILL_VALIDATION", $"保留名称禁止使用：'{n}'。");
        if (
            n is "_index" or "_tmp"
            || n.StartsWith("_", StringComparison.Ordinal)
            || n.Contains("..")
            || n.Contains('/')
            || n.Contains('\\')
        )
            throw new SkillMemoryValidationException("SKILL_VALIDATION", $"非法名称 '{n}'。");
        return n;
    }

    // ---------- 内部实现 ----------

    private string? ResolveDomainRoot(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot
    )
    {
        if (scope == SkillMemoryScope.Global)
            return kind == SkillMemoryKind.Skill
                ? _storage.GlobalSkillsDirectory
                : _storage.GlobalMemoryDirectory;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;
        try
        {
            string wsFull = Path.GetFullPath(workspaceRoot.Trim());
            if (!Directory.Exists(wsFull))
                return null; // E4：工作区根被删/不可读 → Project 视为空
            string halo = ResolveProjectRoot(wsFull);
            if (!Directory.Exists(halo))
                return Path.Combine(halo, kind == SkillMemoryKind.Skill ? "skills" : "memory"); // 容忍不存在，List 视为空
            return Path.Combine(halo, kind == SkillMemoryKind.Skill ? "skills" : "memory");
        }
        catch (SkillMemoryValidationException)
        {
            return null;
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[SkillMemory] Project 域解析失败，视为空域。", ex);
            return null;
        }
    }

    private string ResolveDomainRootOrThrow(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot,
        bool forWrite
    )
    {
        if (scope == SkillMemoryScope.Global)
            return kind == SkillMemoryKind.Skill
                ? _storage.GlobalSkillsDirectory
                : _storage.GlobalMemoryDirectory;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new SkillMemoryValidationException(
                "SKILL_VALIDATION",
                "Project 域必须指定 workspaceRoot。"
            );
        string wsFull = Path.GetFullPath(workspaceRoot.Trim());
        string halo = ResolveProjectRoot(wsFull);
        return Path.Combine(halo, kind == SkillMemoryKind.Skill ? "skills" : "memory");
    }

    private IReadOnlyList<SkillMemoryEntry> ListLocked(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot
    )
    {
        string? domainRoot = ResolveDomainRoot(scope, kind, workspaceRoot);
        if (domainRoot == null || !Directory.Exists(domainRoot))
            return Array.Empty<SkillMemoryEntry>(); // 容忍不存在视为空域
        EnsureIndexHealthyLocked(domainRoot, scope, kind);

        var result = new List<SkillMemoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(domainRoot))
        {
            string rawName = Path.GetFileName(dir) ?? string.Empty;
            // .bak / .deleted 备份目录不在条目之列
            if (rawName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            string normalized;
            try
            {
                normalized = NormalizeAndValidateName(rawName);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"[SkillMemory] 非法条目名已跳过：{rawName}", ex);
                continue;
            }
            if (!seen.Add(normalized))
                continue; // 大小写变体重名目录只取其一
            try
            {
                var entry = TryLoadEntry(dir, normalized, scope, kind);
                if (entry == null)
                    HaloLogger.App.Warn($"[SkillMemory] 条目 '{normalized}' 缺 readme/超限，已跳过。");
                else
                    result.Add(entry);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"[SkillMemory] 条目 '{normalized}' 损坏已跳过。", ex);
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }

    private static string? FindEntryDir(string domainRoot, string normalized)
    {
        string exact = Path.Combine(domainRoot, normalized);
        if (Directory.Exists(exact))
            return exact;
        foreach (var dir in Directory.EnumerateDirectories(domainRoot))
        {
            if (
                string.Equals(Path.GetFileName(dir), normalized, StringComparison.OrdinalIgnoreCase)
                && !(Path.GetFileName(dir) ?? string.Empty).EndsWith(
                    ".bak",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return dir;
        }
        return null;
    }

    private void RemoveCaseVariantDirs(string domainRoot, string normalized)
    {
        foreach (var dir in Directory.EnumerateDirectories(domainRoot))
        {
            string leaf = Path.GetFileName(dir) ?? string.Empty;
            if (
                !string.Equals(leaf, normalized, StringComparison.Ordinal)
                && string.Equals(leaf, normalized, StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                { /* 忽略 */
                }
            }
        }
    }
}
