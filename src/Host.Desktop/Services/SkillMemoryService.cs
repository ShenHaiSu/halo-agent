#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>
/// need09-01：双域 Skill/Memory 存储唯一出入口。
/// 布局：Global=&lt;DataRoot&gt;/skills|memory；Project=&lt;WorkspaceRoot&gt;/.haloagent/skills|memory。
/// </summary>
public sealed partial class SkillMemoryService
{
    public const int MaxReadmeChars = 8000;
    public const int MaxAssetBytes = 512 * 1024;
    public const int MaxDomainTotalChars = 60000;
    public const int MaxEntriesPerDomainKind = 200;
    public const int IndexVersion = 1;

    private static readonly Regex NameRegex =
        new(@"^[a-z0-9][a-z0-9-_]{1,47}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "con",
            "prn",
            "aux",
            "nul",
            "com1",
            "com2",
            "com3",
            "com4",
            "com5",
            "com6",
            "com7",
            "com8",
            "com9",
            "lpt1",
            "lpt2",
            "lpt3",
            "lpt4",
            "lpt5",
            "lpt6",
            "lpt7",
            "lpt8",
            "lpt9",
        };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StoragePathManager _storage;
    private readonly object _syncLock = new();

    // GetMergedForPrompt 5 秒快照缓存（B/C 轨道消费）。
    private string? _mergedCacheKey;
    private string? _mergedCacheSig;
    private DateTime _mergedCacheAtUtc;
    private (
        IReadOnlyList<SkillMemoryEntry> GlobalSkills,
        IReadOnlyList<SkillMemoryEntry> ProjectSkills,
        IReadOnlyList<SkillMemoryEntry> GlobalMemory,
        IReadOnlyList<SkillMemoryEntry> ProjectMemory
    )? _mergedCacheValue;

    public SkillMemoryService(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        EnsureInitialized();
    }

    // ---------- 初始化 ----------

    /// <summary>建 Global 目录 + 空索引（老数据根升级补建，不删旧文件）。</summary>
    public void EnsureInitialized()
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_storage.GlobalSkillsDirectory);
            Directory.CreateDirectory(_storage.GlobalMemoryDirectory);
            EnsureIndexFileLocked(
                Path.Combine(_storage.GlobalSkillsDirectory, "_index.json"),
                SkillMemoryScope.Global,
                SkillMemoryKind.Skill
            );
            EnsureIndexFileLocked(
                Path.Combine(_storage.GlobalMemoryDirectory, "_index.json"),
                SkillMemoryScope.Global,
                SkillMemoryKind.Memory
            );
        }
    }

    /// <summary>建 Project 空域 .haloagent/skills|memory（失败由调用方 Warn吞掉）。实例入口。</summary>
    public void EnsureProjectInitialized(string workspaceRoot) =>
        EnsureProjectInitializedStatic(workspaceRoot);

    /// <summary>建 Project 空域（静态，供 WorkspaceSessionService 调用，避免循环依赖）。</summary>
    public static void EnsureProjectInitializedStatic(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new SkillMemoryValidationException("SKILL_VALIDATION", "工作区根目录不能为空。");
        string haloRoot = ResolveProjectRoot(workspaceRoot);
        Directory.CreateDirectory(Path.Combine(haloRoot, "skills"));
        Directory.CreateDirectory(Path.Combine(haloRoot, "memory"));
        EnsureIndexFile(
            Path.Combine(haloRoot, "skills", "_index.json"),
            SkillMemoryScope.Project,
            SkillMemoryKind.Skill
        );
        EnsureIndexFile(
            Path.Combine(haloRoot, "memory", "_index.json"),
            SkillMemoryScope.Project,
            SkillMemoryKind.Memory
        );
    }

    /// <summary>Project 域根（&lt;WorkspaceRoot&gt;/.haloagent），含工作区根前缀校验。</summary>
    public static string ResolveProjectRoot(string workspaceRoot)
    {
        string wsFull = Path.GetFullPath(workspaceRoot.Trim());
        string halo = Path.GetFullPath(Path.Combine(wsFull, ".haloagent"));
        string prefix =
            wsFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!halo.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new SkillMemoryValidationException("PATH_TRAVERSAL", $"工作区根非法：{workspaceRoot}");
        return halo;
    }

    // ---------- 读 ----------

    /// <summary>列出单域单类全部条目（排序：name 升序；单条损坏跳过 + Warn）。</summary>
    public IReadOnlyList<SkillMemoryEntry> List(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot
    )
    {
        lock (_syncLock)
        {
            return ListLocked(scope, kind, workspaceRoot);
        }
    }

    /// <summary>按名取单条（缺失返回 null；非法名抛 SKILL_VALIDATION）。大小写归一。</summary>
    public SkillMemoryEntry? Get(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string? workspaceRoot
    )
    {
        string normalized = NormalizeAndValidateName(name);
        lock (_syncLock)
        {
            string? domainRoot = ResolveDomainRoot(scope, kind, workspaceRoot);
            if (domainRoot == null || !Directory.Exists(domainRoot))
                return null;
            string? dir = FindEntryDir(domainRoot, normalized);
            if (dir == null)
                return null;
            try
            {
                return TryLoadEntry(dir, normalized, scope, kind);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn($"[SkillMemory] 读取 '{normalized}' 失败已跳过。", ex);
                return null;
            }
        }
    }

    /// <summary>资产绝对路径解析（含 ../ 逃逸校验，违规抛 PATH_TRAVERSAL）。</summary>
    public string ResolveAssetPath(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string assetRelativePath,
        string? workspaceRoot
    )
    {
        string normalized = NormalizeAndValidateName(name);
        if (string.IsNullOrWhiteSpace(assetRelativePath))
            throw new SkillMemoryValidationException("PATH_TRAVERSAL", "资产相对路径不能为空。");
        string rel = assetRelativePath
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel))
            throw new SkillMemoryValidationException(
                "PATH_TRAVERSAL",
                $"禁止绝对路径资产引用：{assetRelativePath}"
            );

        string? domainRoot;
        lock (_syncLock)
        {
            domainRoot = ResolveDomainRoot(scope, kind, workspaceRoot);
        }
        if (domainRoot == null)
            throw new SkillMemoryValidationException("SKILL_NOT_FOUND", $"域不存在：{scope}/{kind}。");
        string? dir = FindEntryDir(domainRoot, normalized);
        if (dir == null)
            throw new SkillMemoryValidationException("SKILL_NOT_FOUND", $"条目不存在：{normalized}。");

        string dirFull = Path.GetFullPath(dir);
        string combined = Path.GetFullPath(Path.Combine(dirFull, rel));
        string prefix = dirFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new SkillMemoryValidationException(
                "PATH_TRAVERSAL",
                $"资产路径逃逸已拒绝：{assetRelativePath}"
            );
        return combined;
    }

    /// <summary>
    /// B/C 契约（逐字遵守）：返回 (globalSkills, projectSkills, globalMemory, projectMemory) 去重排序快照；
    /// Project 同名覆盖 Global（即 global 列表剔除被 project 同名覆盖项）；排序键 scope+name 升序
    /// （各列表单 scope，体现为 name 升序）；5 秒快照缓存。
    /// </summary>
    public (
        IReadOnlyList<SkillMemoryEntry> GlobalSkills,
        IReadOnlyList<SkillMemoryEntry> ProjectSkills,
        IReadOnlyList<SkillMemoryEntry> GlobalMemory,
        IReadOnlyList<SkillMemoryEntry> ProjectMemory
    ) GetMergedForPrompt(string? workspaceRoot)
    {
        lock (_syncLock)
        {
            string wsKey = string.IsNullOrWhiteSpace(workspaceRoot)
                ? string.Empty
                : Path.GetFullPath(workspaceRoot.Trim()).ToLowerInvariant();
            string sig = BuildMergedSignature(wsKey);
            if (
                _mergedCacheValue.HasValue
                && string.Equals(_mergedCacheKey, wsKey, StringComparison.Ordinal)
                && string.Equals(_mergedCacheSig, sig, StringComparison.Ordinal)
                && (DateTime.UtcNow - _mergedCacheAtUtc).TotalSeconds < 5
            )
            {
                return _mergedCacheValue.Value;
            }

            var globalSkills = ListLocked(SkillMemoryScope.Global, SkillMemoryKind.Skill, null);
            var projectSkills = ListLocked(
                SkillMemoryScope.Project,
                SkillMemoryKind.Skill,
                workspaceRoot
            );
            var globalMemory = ListLocked(SkillMemoryScope.Global, SkillMemoryKind.Memory, null);
            var projectMemory = ListLocked(
                SkillMemoryScope.Project,
                SkillMemoryKind.Memory,
                workspaceRoot
            );

            // Project 同名覆盖 Global：global 侧剔除被覆盖项。
            var projectSkillNames = new HashSet<string>(
                projectSkills.Select(e => e.Name),
                StringComparer.Ordinal
            );
            var projectMemoryNames = new HashSet<string>(
                projectMemory.Select(e => e.Name),
                StringComparer.Ordinal
            );
            var mergedGlobalSkills = globalSkills
                .Where(e => !projectSkillNames.Contains(e.Name))
                .ToList();
            var mergedGlobalMemory = globalMemory
                .Where(e => !projectMemoryNames.Contains(e.Name))
                .ToList();

            var result = (
                (IReadOnlyList<SkillMemoryEntry>)mergedGlobalSkills,
                (IReadOnlyList<SkillMemoryEntry>)projectSkills,
                (IReadOnlyList<SkillMemoryEntry>)mergedGlobalMemory,
                (IReadOnlyList<SkillMemoryEntry>)projectMemory
            );
            _mergedCacheKey = wsKey;
            _mergedCacheSig = sig;
            _mergedCacheAtUtc = DateTime.UtcNow;
            _mergedCacheValue = result;
            return result;
        }
    }

    // ---------- 写 ----------

    /// <summary>新建或更新单条（原子写 + 索引重建 + 配额校验；updated_at 服务端强制 UtcNow）。</summary>
    public SkillMemoryEntry CreateOrUpdate(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string name,
        string bodyMarkdown,
        string? workspaceRoot = null,
        bool enabled = true,
        int priority = 100,
        int version = 1,
        string pin = "auto",
        string? title = null,
        string? summary = null
    )
    {
        string normalized = NormalizeAndValidateName(name);
        string body = (bodyMarkdown ?? string.Empty).Replace("\r\n", "\n");
        if (body.Length > MaxReadmeChars)
            throw new SkillMemoryValidationException(
                "SKILL_VALIDATION",
                $"正文 {body.Length} 字符超限（上限 {MaxReadmeChars}），请截断到第 {MaxReadmeChars} 字符。"
            );
        string normalizedPin = NormalizePin(kind, pin);

        lock (_syncLock)
        {
            string domainRoot = ResolveDomainRootOrThrow(
                scope,
                kind,
                workspaceRoot,
                forWrite: true
            );
            Directory.CreateDirectory(domainRoot);
            RemoveCaseVariantDirs(domainRoot, normalized);

            // 配额：数量上限 + 单域单类总字符。
            var existing = ListLocked(scope, kind, workspaceRoot);
            var same = existing.FirstOrDefault(e =>
                string.Equals(e.Name, normalized, StringComparison.Ordinal)
            );
            if (same == null && existing.Count >= MaxEntriesPerDomainKind)
                throw new SkillMemoryValidationException(
                    "SKILL_VALIDATION",
                    $"单域单类已达 {MaxEntriesPerDomainKind} 条上限，请先删除或归档。"
                );
            int totalChars =
                existing
                    .Where(e => !string.Equals(e.Name, normalized, StringComparison.Ordinal))
                    .Sum(e => e.BodyMarkdown.Length) + body.Length;
            if (totalChars > MaxDomainTotalChars)
                throw new SkillMemoryValidationException(
                    "SKILL_VALIDATION",
                    $"单域单类总字符 {totalChars} 超限（上限 {MaxDomainTotalChars}），请先删除或归档。"
                );

            string dir = Path.Combine(domainRoot, normalized);
            Directory.CreateDirectory(dir);

            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? DeriveTitle(body)
                : title.Trim();
            string resolvedSummary = string.IsNullOrWhiteSpace(summary)
                ? DeriveSummary(body)
                : summary.Trim();
            DateTime now = DateTime.UtcNow;

            string readme = BuildReadme(
                version,
                enabled,
                priority,
                normalizedPin,
                resolvedSummary,
                now,
                body
            );
            WriteTextAtomic(Path.Combine(dir, "readme.md"), readme);

            string metaFile = kind == SkillMemoryKind.Skill ? "skill.json" : "memory.json";
            var metaObj = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["enabled"] = enabled,
                ["priority"] = priority,
                ["pin"] = normalizedPin,
                ["title"] = resolvedTitle,
                ["summary"] = resolvedSummary,
                ["updatedAtUtc"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            AtomicFileWriter.WriteJsonAtomic(Path.Combine(dir, metaFile), metaObj);

            RebuildIndexLocked(scope, kind, workspaceRoot);
            InvalidateMergedCacheLocked();

            return TryLoadEntry(dir, normalized, scope, kind)
                ?? throw new SkillMemoryValidationException(
                    "SKILL_VALIDATION",
                    $"写入后回读失败：{normalized}。"
                );
        }
    }
}
