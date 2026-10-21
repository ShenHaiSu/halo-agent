#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>need09-01：Skill/Memory 域（Global 随数据根走 / Project 随工作区 .haloagent/ 走）。</summary>
public enum SkillMemoryScope
{
    Global,
    Project
}

/// <summary>need09-01：条目类别。</summary>
public enum SkillMemoryKind
{
    Skill,
    Memory
}

/// <summary>need09-01：Skill/Memory 校验失败（非法名/超限/遍历逃逸等）。Message 恒含 Code 前缀。</summary>
public sealed class SkillMemoryValidationException : Exception
{
    public string Code { get; }

    public SkillMemoryValidationException(string code, string message)
        : base($"[{code}] {message}")
    {
        Code = code;
    }
}

/// <summary>need09-01：单条 Skill/Memory 快照。Scope 取值 "global"|"project"。</summary>
public sealed record SkillMemoryEntry(
    string Name,
    string Scope,
    string Title,
    string Summary,
    string BodyMarkdown,
    string Pin,
    bool Enabled,
    int Priority,
    int Version,
    int EstTokens,
    string Hash,
    bool HasAssets,
    string DirPath,
    DateTime UpdatedAtUtc
)
{
    public SkillMemoryKind Kind { get; init; } = SkillMemoryKind.Skill;
}

/// <summary>
/// need09-01：双域 Skill/Memory 存储唯一出入口。
/// 布局：Global=&lt;DataRoot&gt;/skills|memory；Project=&lt;WorkspaceRoot&gt;/.haloagent/skills|memory。
/// </summary>
public sealed class SkillMemoryService
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

    private sealed class FrontMeta
    {
        public int Version = 1;
        public bool Enabled = true;
        public int Priority = 100;
        public string Pin = "auto";
        public string? ScopeHint;
        public DateTime? UpdatedAtUtc;
    }

    private SkillMemoryEntry? TryLoadEntry(
        string dir,
        string normalized,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        string readmePath = Path.Combine(dir, "readme.md");
        if (!File.Exists(readmePath))
            return null; // E2：缺 readme → 跳过
        string raw = File.ReadAllText(readmePath, Utf8NoBom);
        var (front, bodyRaw) = ParseReadme(raw);
        string body = bodyRaw.Replace("\r\n", "\n");
        if (body.Length > MaxReadmeChars)
            return null; // 超限条目读路径跳过（写路径拒绝）

        // skill.json / memory.json 优先合并（缺键取 frontmatter，仍缺取默认）。
        int version = front.Version;
        bool enabled = front.Enabled;
        int priority = front.Priority;
        string pin = front.Pin;
        string? title = null;
        string? summary = front.ScopeHint;
        DateTime? updatedAt = front.UpdatedAtUtc;
        string metaFile = Path.Combine(
            dir,
            kind == SkillMemoryKind.Skill ? "skill.json" : "memory.json"
        );
        if (File.Exists(metaFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile, Utf8NoBom));
                var root = doc.RootElement;
                version = GetJsonInt(root, "version", version);
                enabled = GetJsonBool(root, "enabled", enabled);
                priority = GetJsonInt(root, "priority", priority);
                pin = GetJsonString(root, "pin", pin) ?? pin;
                title = GetJsonString(root, "title", title);
                summary =
                    GetJsonString(root, "summary", summary)
                    ?? GetJsonString(root, "scopeHint", summary)
                    ?? GetJsonString(root, "scope_hint", summary);
                string? ts =
                    GetJsonString(root, "updatedAtUtc", null)
                    ?? GetJsonString(root, "updated_at", null);
                if (!string.IsNullOrWhiteSpace(ts) && DateTime.TryParse(ts, out var parsed))
                    updatedAt = parsed.ToUniversalTime();
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn(
                    $"[SkillMemory] 条目 '{normalized}' 元数据 JSON 损坏，已用 frontmatter 兜底。",
                    ex
                );
            }
        }

        pin = NormalizePin(kind, pin);
        title = string.IsNullOrWhiteSpace(title) ? DeriveTitle(body) : title.Trim();
        summary = string.IsNullOrWhiteSpace(summary) ? DeriveSummary(body) : summary.Trim();
        string normalizedBody = body.TrimEnd();
        string hash = HeaderVariableResolver.ComputeDeterministicHash16(normalizedBody);
        string assetsDir = Path.Combine(dir, "assets");
        bool hasAssets =
            Directory.Exists(assetsDir)
            && Directory.EnumerateFiles(assetsDir, "*", SearchOption.AllDirectories).Any();
        DateTime mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(readmePath);
        }
        catch
        {
            mtime = DateTime.UtcNow;
        }

        return new SkillMemoryEntry(
            normalized,
            scope == SkillMemoryScope.Global ? "global" : "project",
            title,
            summary,
            body,
            pin,
            enabled,
            priority,
            version,
            (body.Length + 3) / 4,
            hash,
            hasAssets,
            Path.GetFullPath(dir),
            updatedAt ?? mtime
        ) with
        {
            Kind = kind
        };
    }

    private static (FrontMeta Meta, string Body) ParseReadme(string raw)
    {
        var meta = new FrontMeta();
        string text = (raw ?? string.Empty).Replace("\r\n", "\n").TrimStart('\uFEFF');
        if (
            !text.StartsWith("---\n", StringComparison.Ordinal)
            && !text.StartsWith("---\r\n", StringComparison.Ordinal)
        )
            return (meta, text);
        string[] lines = text.Split('\n');
        int end = -1;
        for (int i = 1; i < lines.Length && i < 60; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }
        if (end < 0)
            return (meta, text); // 无闭合则全文即正文
        for (int i = 1; i < end; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string val = Unquote(line[(colon + 1)..].Trim());
            switch (key)
            {
                case "version":
                    if (int.TryParse(val, out var v))
                        meta.Version = v;
                    break;
                case "enabled":
                    if (bool.TryParse(val, out var b))
                        meta.Enabled = b;
                    else if (val == "1" || val.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        meta.Enabled = true;
                    else if (val == "0" || val.Equals("no", StringComparison.OrdinalIgnoreCase))
                        meta.Enabled = false;
                    break;
                case "priority":
                    if (int.TryParse(val, out var p))
                        meta.Priority = p;
                    break;
                case "pin":
                    if (!string.IsNullOrWhiteSpace(val))
                        meta.Pin = val.ToLowerInvariant();
                    break;
                case "scope_hint":
                case "scopehint":
                    meta.ScopeHint = val;
                    break;
                case "updated_at":
                case "updatedatutc":
                    if (DateTime.TryParse(val, out var dt))
                        meta.UpdatedAtUtc = dt.ToUniversalTime();
                    break;
                default:
                    break; // 未知键保留但忽略
            }
        }
        return (meta, string.Join("\n", lines.Skip(end + 1)));
    }

    private static string Unquote(string v)
    {
        if (
            v.Length >= 2
            && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\'')))
        )
            return v[1..^1];
        return v;
    }

    private static string? GetJsonString(JsonElement root, string key, string? fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String
            )
                return prop.Value.GetString() ?? fallback;
        }
        return fallback;
    }

    private static int GetJsonInt(JsonElement root, string key, int fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out var v)
            )
                return v;
        }
        return fallback;
    }

    private static bool GetJsonBool(JsonElement root, string key, bool fallback)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.Value.ValueKind == JsonValueKind.False)
                    return false;
            }
        }
        return fallback;
    }

    private static string NormalizePin(SkillMemoryKind kind, string? pin)
    {
        string p = (pin ?? "auto").Trim().ToLowerInvariant();
        if (kind == SkillMemoryKind.Skill)
            return (p == "full" || p == "auto") ? p : "auto";
        return (p == "pinned" || p == "archived" || p == "auto") ? p : "auto";
    }

    private static string DeriveTitle(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            string t = line.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrEmpty(t))
                return t.Length <= 80 ? t : t[..80];
        }
        return "Untitled";
    }

    private static string DeriveSummary(string body)
    {
        string flat = string.Join(
            " ",
            body.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l))
        );
        if (flat.StartsWith('#'))
            flat = flat.TrimStart('#').Trim();
        int dot = flat.IndexOfAny(new[] { '。', '！', '？', '!', '?', '.', ';', '；' });
        string first = dot >= 0 ? flat[..(dot + 1)].Trim() : flat.Trim();
        if (first.Length > 120)
            first = first[..120];
        return string.IsNullOrEmpty(first) ? "（空）" : first;
    }

    private static string BuildReadme(
        int version,
        bool enabled,
        int priority,
        string pin,
        string scopeHint,
        DateTime updatedAt,
        string body
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"version: {version}");
        sb.AppendLine($"enabled: {(enabled ? "true" : "false")}");
        sb.AppendLine($"priority: {priority}");
        sb.AppendLine($"pin: {pin}");
        sb.AppendLine($"scope_hint: \"{scopeHint.Replace("\"", "'")}\"");
        sb.AppendLine($"updated_at: \"{updatedAt:yyyy-MM-ddTHH:mm:ssZ}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body.TrimStart('\n'));
        if (!body.EndsWith('\n'))
            sb.AppendLine();
        return sb.ToString();
    }

    // ---------- 索引 ----------

    private void EnsureIndexHealthyLocked(
        string domainRoot,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        string indexPath = Path.Combine(domainRoot, "_index.json");
        try
        {
            if (!File.Exists(indexPath))
            {
                RebuildIndexLocked(scope, kind, null, domainRoot);
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath, Utf8NoBom));
            if (
                !doc.RootElement.TryGetProperty("version", out var v)
                || v.GetInt32() != IndexVersion
            )
                throw new InvalidDataException($"_index.json version != {IndexVersion}");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[SkillMemory] _index.json 损坏/过时，已重建：{indexPath}", ex);
            try
            {
                RebuildIndexLocked(scope, kind, null, domainRoot);
            }
            catch (Exception ex2)
            {
                HaloLogger.App.Warn("[SkillMemory] 索引重建失败。", ex2);
            }
        }
    }

    private void RebuildIndexLocked(
        SkillMemoryScope scope,
        SkillMemoryKind kind,
        string? workspaceRoot,
        string? knownRoot = null
    )
    {
        string? domainRoot = knownRoot ?? ResolveDomainRoot(scope, kind, workspaceRoot);
        if (domainRoot == null)
            return;
        Directory.CreateDirectory(domainRoot);

        var entries = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(domainRoot))
        {
            string rawName = Path.GetFileName(dir) ?? string.Empty;
            if (rawName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            string normalized;
            try
            {
                normalized = NormalizeAndValidateName(rawName);
            }
            catch
            {
                continue;
            }
            if (!seen.Add(normalized))
                continue;
            try
            {
                var e = TryLoadEntry(dir, normalized, scope, kind);
                if (e == null)
                    continue;
                string summary = e.Summary.Length <= 120 ? e.Summary : e.Summary[..120];
                entries.Add(
                    new Dictionary<string, object?>
                    {
                        ["name"] = e.Name,
                        ["scope"] = e.Scope,
                        ["enabled"] = e.Enabled,
                        ["pin"] = e.Pin,
                        ["priority"] = e.Priority,
                        ["version"] = e.Version,
                        ["title"] = e.Title,
                        ["summary"] = summary,
                        ["chars"] = e.BodyMarkdown.Length,
                        ["estTokens"] = e.EstTokens,
                        ["hash"] = e.Hash,
                        ["hasAssets"] = e.HasAssets,
                    }
                );
            }
            catch
            { /* 单条损坏跳过 */
            }
        }
        entries.Sort(
            (a, b) =>
                string.Compare((string)a["name"]!, (string)b["name"]!, StringComparison.Ordinal)
        );

        var index = new Dictionary<string, object?>
        {
            ["version"] = IndexVersion,
            ["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["scope"] = scope == SkillMemoryScope.Global ? "global" : "project",
            ["kind"] = kind == SkillMemoryKind.Skill ? "skill" : "memory",
        };
        if (kind == SkillMemoryKind.Skill)
            index["skills"] = entries;
        else
            index["memories"] = entries;

        WriteJsonAtomic(Path.Combine(domainRoot, "_index.json"), index);
    }

    private static void EnsureIndexFileLocked(
        string indexPath,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        try
        {
            string? dir = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            EnsureIndexFile(indexPath, scope, kind);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[SkillMemory] 索引初始化失败：{indexPath}", ex);
        }
    }

    private static void EnsureIndexFile(
        string indexPath,
        SkillMemoryScope scope,
        SkillMemoryKind kind
    )
    {
        if (File.Exists(indexPath))
            return;
        var index = new Dictionary<string, object?>
        {
            ["version"] = IndexVersion,
            ["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["scope"] = scope == SkillMemoryScope.Global ? "global" : "project",
            ["kind"] = kind == SkillMemoryKind.Skill ? "skill" : "memory",
        };
        if (kind == SkillMemoryKind.Skill)
            index["skills"] = new List<object>();
        else
            index["memories"] = new List<object>();
        WriteJsonAtomic(indexPath, index);
    }

    private string BuildMergedSignature(string wsKey)
    {
        var sb = new StringBuilder(wsKey);
        sb.Append('|');
        foreach (
            var p in new[]
            {
                Path.Combine(_storage.GlobalSkillsDirectory, "_index.json"),
                Path.Combine(_storage.GlobalMemoryDirectory, "_index.json"),
            }
        )
        {
            sb.Append(SigOf(p)).Append(';');
        }
        if (!string.IsNullOrEmpty(wsKey))
        {
            try
            {
                string halo = ResolveProjectRoot(wsKey);
                sb.Append(SigOf(Path.Combine(halo, "skills", "_index.json"))).Append(';');
                sb.Append(SigOf(Path.Combine(halo, "memory", "_index.json"))).Append(';');
            }
            catch
            {
                sb.Append("bad;");
            }
        }
        return sb.ToString();
    }

    private static string SigOf(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "∅";
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
        }
        catch
        {
            return "?";
        }
    }

    private void InvalidateMergedCacheLocked()
    {
        _mergedCacheKey = null;
        _mergedCacheSig = null;
        _mergedCacheValue = null;
    }

    // ---------- 原子写盘 ----------

    private static void WriteTextAtomic(string filePath, string content)
    {
        string full = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                { /* 忽略残留 */
                }
            }
        }
    }

    private static void WriteBytesAtomic(string filePath, byte[] content)
    {
        string full = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                { /* 忽略残留 */
                }
            }
        }
    }

    private static void WriteJsonAtomic(string filePath, object data)
    {
        string json = JsonSerializer.Serialize(
            data,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }
        );
        WriteTextAtomic(filePath, json);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, Path.GetFullPath(file));
            string target = Path.Combine(dest, rel);
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
    }
}
