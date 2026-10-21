using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;


/// <summary>need21-B02: SkillMemory 垂直切片（DTO 见 SkillMemoryModels；OpenSkillMemoryFolder 已移入 ShellBridge）。</summary>
public sealed partial class HostBridgeFacade
{
    private SkillMemoryService? _skillMemoryServiceOpt;

    private SkillMemoryService SkillMemoryServiceInternal =>
        _skillMemoryServiceOpt ??= new SkillMemoryService(_storageManager);

    private static SkillMemoryKind ParseSkillKind(string kind)
    {
        string k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (k == "memory")
            return SkillMemoryKind.Memory;
        if (k == "skill")
            return SkillMemoryKind.Skill;
        throw new SkillMemoryValidationException(
            "SKILL_VALIDATION",
            $"未知 kind：'{kind}'（仅支持 skill|memory）。"
        );
    }

    private async Task<string?> ResolveWorkspaceRootAsync(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;
        try
        {
            var ws = await _workspaceSessionService
                .GetWorkspaceAsync(workspaceId.Trim())
                .ConfigureAwait(false);
            if (ws == null || string.IsNullOrWhiteSpace(ws.RootPath))
                return null;
            return ws.RootPath;
        }
        catch
        {
            return null;
        }
    }

    private static SkillMemoryListItemDto ToListDto(SkillMemoryEntry e) =>
        new()
        {
            Name = e.Name,
            Scope = e.Scope,
            Title = e.Title,
            Summary = e.Summary,
            Pin = e.Pin,
            Enabled = e.Enabled,
            Priority = e.Priority,
            Version = e.Version,
            EstTokens = e.EstTokens,
            Chars = e.BodyMarkdown.Length,
            Hash = e.Hash,
            HasAssets = e.HasAssets,
            UpdatedAtUtc = e.UpdatedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            DirPath = e.DirPath,
        };

    private string? FindLatestBak(
        string scope,
        SkillMemoryKind kind,
        string normalized,
        string? workspaceRoot
    )
    {
        try
        {
            string? domainRoot =
                scope == "global"
                    ? (
                        kind == SkillMemoryKind.Skill
                            ? _storageManager.GlobalSkillsDirectory
                            : _storageManager.GlobalMemoryDirectory
                    )
                    : ResolveProjectDomainRoot(workspaceRoot, kind);
            if (domainRoot == null || !Directory.Exists(domainRoot))
                return null;
            return Directory
                .EnumerateFileSystemEntries(domainRoot, normalized + ".deleted-*.bak")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveProjectDomainRoot(string? workspaceRoot, SkillMemoryKind kind)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;
        try
        {
            string halo = SkillMemoryService.ResolveProjectRoot(
                Path.GetFullPath(workspaceRoot.Trim())
            );
            return Path.Combine(halo, kind == SkillMemoryKind.Skill ? "skills" : "memory");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>need10-02：列表。scope=all 拼 Global+Project（Project 同名覆盖 Global），name 升序。无工作区+project → Project 视为空。</summary>
    public async Task<BridgeResponse<List<SkillMemoryListItemDto>>> ListSkillMemory(
        string kind,
        string scope,
        string? workspaceId
    )
    {
        try
        {
            var k = ParseSkillKind(kind);
            string s = (scope ?? "all").Trim().ToLowerInvariant();
            if (s != "global" && s != "project" && s != "all")
                return BridgeResponse<List<SkillMemoryListItemDto>>.Fail(
                    "SKILL_VALIDATION",
                    $"未知 scope：'{scope}'（仅支持 global|project|all）。"
                );
            string? wsRoot = await ResolveWorkspaceRootAsync(workspaceId).ConfigureAwait(false);
            var svc = SkillMemoryServiceInternal;

            if (s == "global")
                return BridgeResponse<List<SkillMemoryListItemDto>>.Success(
                    svc.List(SkillMemoryScope.Global, k, null).Select(ToListDto).ToList()
                );
            if (s == "project")
                return BridgeResponse<List<SkillMemoryListItemDto>>.Success(
                    svc.List(SkillMemoryScope.Project, k, wsRoot).Select(ToListDto).ToList(),
                    wsRoot == null ? "(no workspace)" : null
                );

            var globals = svc.List(SkillMemoryScope.Global, k, null);
            var projects =
                wsRoot == null
                    ? Array.Empty<SkillMemoryEntry>()
                    : svc.List(SkillMemoryScope.Project, k, wsRoot);
            var projectNames = new HashSet<string>(
                projects.Select(e => e.Name),
                StringComparer.Ordinal
            );
            var merged = globals
                .Where(e => !projectNames.Contains(e.Name))
                .Concat(projects)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .Select(ToListDto)
                .ToList();
            return BridgeResponse<List<SkillMemoryListItemDto>>.Success(
                merged,
                wsRoot == null ? "(no workspace)" : null
            );
        }
        catch (SkillMemoryValidationException vex)
        {
            return BridgeResponse<List<SkillMemoryListItemDto>>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ListSkillMemory failed", ex);
            return BridgeResponse<List<SkillMemoryListItemDto>>.Fail(
                "SKILL_BRIDGE_ERROR",
                ex.Message
            );
        }
    }

    /// <summary>need10-02：详情。正文截断 8000→前 800 字 + truncated。</summary>
    public async Task<BridgeResponse<SkillMemoryDetailDto?>> GetSkillMemoryDetail(
        string kind,
        string scope,
        string name,
        string? workspaceId
    )
    {
        try
        {
            var k = ParseSkillKind(kind);
            string s = (scope ?? string.Empty).Trim().ToLowerInvariant();
            if (s != "global" && s != "project")
                return BridgeResponse<SkillMemoryDetailDto?>.Fail(
                    "SKILL_VALIDATION",
                    $"详情 scope 须为 global|project（收到 '{scope}'）。"
                );
            string? wsRoot = await ResolveWorkspaceRootAsync(workspaceId).ConfigureAwait(false);
            var sc = s == "global" ? SkillMemoryScope.Global : SkillMemoryScope.Project;
            var entry = SkillMemoryServiceInternal.Get(sc, k, name ?? string.Empty, wsRoot);
            if (entry == null)
                return BridgeResponse<SkillMemoryDetailDto?>.Fail(
                    "SKILL_NOT_FOUND",
                    $"条目不存在：{(name ?? string.Empty).Trim()}。"
                );
            var dto = ToListDto(entry);
            const int previewLen = 800;
            string body = entry.BodyMarkdown ?? string.Empty;
            var detail = new SkillMemoryDetailDto
            {
                Name = dto.Name,
                Scope = dto.Scope,
                Title = dto.Title,
                Summary = dto.Summary,
                Pin = dto.Pin,
                Enabled = dto.Enabled,
                Priority = dto.Priority,
                Version = dto.Version,
                EstTokens = dto.EstTokens,
                Chars = dto.Chars,
                Hash = dto.Hash,
                HasAssets = dto.HasAssets,
                UpdatedAtUtc = dto.UpdatedAtUtc,
                DirPath = dto.DirPath,
                BodyTotalChars = body.Length,
                Truncated = body.Length > previewLen,
                BodyPreview = body.Length > previewLen ? body.Substring(0, previewLen) : body,
            };
            return BridgeResponse<SkillMemoryDetailDto?>.Success(detail);
        }
        catch (SkillMemoryValidationException vex)
        {
            return BridgeResponse<SkillMemoryDetailDto?>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetSkillMemoryDetail failed", ex);
            return BridgeResponse<SkillMemoryDetailDto?>.Fail("SKILL_BRIDGE_ERROR", ex.Message);
        }
    }

    /// <summary>need10-02：开关。scope 必须 global|project；成功 Publish config:skill-memory-changed。</summary>
    public async Task<BridgeResponse<SkillMemoryOpResult>> SetSkillMemoryEnabled(
        string kind,
        string scope,
        string name,
        bool enabled,
        string? workspaceId
    )
    {
        try
        {
            var k = ParseSkillKind(kind);
            string s = (scope ?? string.Empty).Trim().ToLowerInvariant();
            if (s != "global" && s != "project")
                return BridgeResponse<SkillMemoryOpResult>.Fail(
                    "SKILL_VALIDATION",
                    "写操作 scope 须明确为 global|project（all 已拒绝）。"
                );
            string? wsRoot = await ResolveWorkspaceRootAsync(workspaceId).ConfigureAwait(false);
            if (s == "project" && string.IsNullOrWhiteSpace(wsRoot))
                return BridgeResponse<SkillMemoryOpResult>.Fail(
                    "SKILL_VALIDATION",
                    "Project 域须先选工作区或改用 global。"
                );
            var sc = s == "global" ? SkillMemoryScope.Global : SkillMemoryScope.Project;
            try
            {
                SkillMemoryServiceInternal.SetEnabled(sc, k, name ?? string.Empty, wsRoot, enabled);
            }
            catch (SkillMemoryValidationException vex)
            {
                return BridgeResponse<SkillMemoryOpResult>.Fail(vex.Code, vex.Message);
            }
            string normalized = SkillMemoryService.NormalizeAndValidateName(name);
            JsEventDispatcher.Publish(
                "config:skill-memory-changed",
                new
                {
                    kind = k == SkillMemoryKind.Skill ? "skill" : "memory",
                    scope = s,
                    name = normalized
                }
            );
            string action = enabled ? "启用" : "停用";
            string kindCn = k == SkillMemoryKind.Skill ? "技能" : "记忆";
            return BridgeResponse<SkillMemoryOpResult>.Success(
                new SkillMemoryOpResult
                {
                    Ok = true,
                    Notice = $"已{action} {kindCn} '{normalized}'（{s}）。下一轮对话起生效。",
                }
            );
        }
        catch (SkillMemoryValidationException vex)
        {
            return BridgeResponse<SkillMemoryOpResult>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetSkillMemoryEnabled failed", ex);
            return BridgeResponse<SkillMemoryOpResult>.Fail("SKILL_BRIDGE_ERROR", ex.Message);
        }
    }

    /// <summary>need10-02：删除（留 .bak）。scope 必须 global|project；成功 Publish config:skill-memory-changed。</summary>
    public async Task<BridgeResponse<SkillMemoryOpResult>> DeleteSkillMemory(
        string kind,
        string scope,
        string name,
        string? workspaceId
    )
    {
        try
        {
            var k = ParseSkillKind(kind);
            string s = (scope ?? string.Empty).Trim().ToLowerInvariant();
            if (s != "global" && s != "project")
                return BridgeResponse<SkillMemoryOpResult>.Fail(
                    "SKILL_VALIDATION",
                    "写操作 scope 须明确为 global|project（all 已拒绝）。"
                );
            string? wsRoot = await ResolveWorkspaceRootAsync(workspaceId).ConfigureAwait(false);
            if (s == "project" && string.IsNullOrWhiteSpace(wsRoot))
                return BridgeResponse<SkillMemoryOpResult>.Fail(
                    "SKILL_VALIDATION",
                    "Project 域须先选工作区或改用 global。"
                );
            var sc = s == "global" ? SkillMemoryScope.Global : SkillMemoryScope.Project;
            string normalized = SkillMemoryService.NormalizeAndValidateName(name ?? string.Empty);
            try
            {
                SkillMemoryServiceInternal.Delete(sc, k, normalized, wsRoot);
            }
            catch (SkillMemoryValidationException vex)
            {
                return BridgeResponse<SkillMemoryOpResult>.Fail(vex.Code, vex.Message);
            }
            string? bak = FindLatestBak(s, k, normalized, wsRoot);
            JsEventDispatcher.Publish(
                "config:skill-memory-changed",
                new
                {
                    kind = k == SkillMemoryKind.Skill ? "skill" : "memory",
                    scope = s,
                    name = normalized
                }
            );
            string kindCn = k == SkillMemoryKind.Skill ? "技能" : "记忆";
            return BridgeResponse<SkillMemoryOpResult>.Success(
                new SkillMemoryOpResult
                {
                    Ok = true,
                    BakFileName = bak,
                    Notice = $"已删除{s} {kindCn} '{normalized}'（备份 {bak ?? "无"}）。下一轮对话起生效。",
                }
            );
        }
        catch (SkillMemoryValidationException vex)
        {
            return BridgeResponse<SkillMemoryOpResult>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("DeleteSkillMemory failed", ex);
            return BridgeResponse<SkillMemoryOpResult>.Fail("SKILL_BRIDGE_ERROR", ex.Message);
        }
    }
}
