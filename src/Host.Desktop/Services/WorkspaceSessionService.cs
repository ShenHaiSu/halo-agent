using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

public interface IWorkspaceSessionService
{
    Task<List<WorkspaceInfo>> GetAllWorkspacesAsync(CancellationToken ct = default);
    Task<WorkspaceInfo?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default);
    Task<WorkspaceInfo> AddOrUpdateWorkspaceAsync(
        string rootPath,
        string? customName = null,
        CancellationToken ct = default
    );
    Task<bool> RemoveWorkspaceAsync(
        string workspaceId,
        bool deletePhysicalSessionFolder = true,
        CancellationToken ct = default
    );
    Task<bool> SetWorkspacePreferenceAsync(
        string workspaceId,
        string? providerId,
        string? modelId,
        CancellationToken ct = default
    );
    Task<bool> TouchWorkspaceAsync(string workspaceId, CancellationToken ct = default);

    Task<List<SessionMetadata>> GetSessionsByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    );
    Task<SessionMetadata> CreateSessionAsync(
        string workspaceId,
        string? title = null,
        string? providerId = null,
        string? modelId = null,
        CancellationToken ct = default
    );

    /// <summary>need13：建子会话（扁平寄宿；老 CreateSessionAsync 委托此方法 parent=null）。</summary>
    Task<SessionMetadata> CreateSubSessionAsync(
        string workspaceId,
        string? title,
        string? providerId,
        string? modelId,
        string? parentSessionId,
        string agentRole,
        string? subLabel,
        string? createdByCallId,
        string? taskPrompt,
        CancellationToken ct = default
    );

    /// <summary>need13：查主下全部子（创建时间正序）。</summary>
    Task<List<SessionMetadata>> GetSubSessionsAsync(
        string workspaceId,
        string parentSessionId,
        CancellationToken ct = default
    );
    Task<SessionMetadata?> GetSubSessionAsync(
        string workspaceId,
        string parentSessionId,
        string subSessionId,
        CancellationToken ct = default
    );

    /// <summary>need13：级联删树（先取消由 Bridge 层调 CancelTree，再删子盘+主盘）。</summary>
    Task<bool> DeleteSessionTreeAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<SessionMetadata?> GetSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<List<UnifiedChatMessage>> GetSessionMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<bool> AppendSessionMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    );

    /// <summary>need07：重放截断（removeAnchor=true 为重放模式：按前驱 user 切面并返回复发原文）。</summary>
    Task<HistoryTruncateResult> TruncateSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        bool removeAnchor,
        CancellationToken ct = default
    );

    /// <summary>need07：分支（includeAnchor=true 含锚轮，false 排除锚点）。</summary>
    Task<SessionMetadata> BranchSessionAsync(
        string workspaceId,
        string sourceSessionId,
        string anchorMessageId,
        bool includeAnchor,
        CancellationToken ct = default
    );

    /// <summary>孤儿 tool_calls 愈合：缺回包的补 synthetic cancelled tool，无主 tool 消息直接清理。</summary>
    Task<HistoryHealResult> HealSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>删除单条消息（删后顺带愈合，保证历史仍可直接发模型）。</summary>
    Task<HistoryDeleteResult> DeleteSessionMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        CancellationToken ct = default
    );
    Task<bool> UpdateSessionTitleAsync(
        string workspaceId,
        string sessionId,
        string newTitle,
        CancellationToken ct = default
    );
    Task<bool> SetSessionPinnedAsync(
        string workspaceId,
        string sessionId,
        bool isPinned,
        CancellationToken ct = default
    );
    Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
    Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
}

/// <summary>
/// 工作区与会话领域服务实现
/// </summary>
public sealed class WorkspaceSessionService : IWorkspaceSessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionAppendLocks = new();

    public WorkspaceSessionService(ISessionRepository sessionRepository)
    {
        _sessionRepository =
            sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
    }

    /// <summary>need05-03：供压缩服务做备份/原子重写（调用方须持有追加锁）。</summary>
    internal ISessionRepository SessionRepository => _sessionRepository;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        string fullPath = Path.GetFullPath(path.Trim());
        string normalized = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        if (string.IsNullOrEmpty(normalized) || normalized.EndsWith(':'))
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }

    public async Task<List<WorkspaceInfo>> GetAllWorkspacesAsync(CancellationToken ct = default)
    {
        var list = await _sessionRepository.LoadWorkspacesIndexAsync(ct).ConfigureAwait(false);
        return list.OrderByDescending(w => w.IsPinned)
            .ThenByDescending(w => w.LastOpenedAtUtc)
            .ToList();
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;
        var list = await _sessionRepository.LoadWorkspacesIndexAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault(w =>
            string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
        );
    }

    public async Task<WorkspaceInfo> AddOrUpdateWorkspaceAsync(
        string rootPath,
        string? customName = null,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Workspace root path cannot be empty.", nameof(rootPath));
        }

        string normalizedPath = NormalizePath(rootPath);
        var workspaces = await _sessionRepository
            .LoadWorkspacesIndexAsync(ct)
            .ConfigureAwait(false);

        // 按不区分大小写检查是否已存在
        var existing = workspaces.FirstOrDefault(w =>
            string.Equals(
                NormalizePath(w.RootPath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (existing != null)
        {
            existing.LastOpenedAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(customName))
            {
                existing.Name = customName.Trim();
            }
            await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);
            TryEnsureProjectSkillMemory(normalizedPath);
            return existing;
        }

        // 新建工作区
        string name = !string.IsNullOrWhiteSpace(customName)
            ? customName.Trim()
            : Path.GetFileName(
                normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );

        if (string.IsNullOrWhiteSpace(name))
        {
            name = normalizedPath;
        }

        string workspaceHash = HeaderVariableResolver.ComputeDeterministicHash16(normalizedPath);

        var newWorkspace = new WorkspaceInfo
        {
            Name = name,
            RootPath = normalizedPath,
            WorkspaceHash = workspaceHash,
            CreatedAtUtc = DateTime.UtcNow,
            LastOpenedAtUtc = DateTime.UtcNow
        };

        workspaces.Add(newWorkspace);
        await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);

        // 若该工作区下尚无会话，自动初始化首个会话
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(newWorkspace.Id, ct)
            .ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            await CreateSessionAsync(newWorkspace.Id, "新建会话", ct: ct).ConfigureAwait(false);
        }

        // need09-01：新建工作区后建空 .haloagent/skills|memory（失败只 Warn，不阻塞绑定）
        TryEnsureProjectSkillMemory(normalizedPath);

        return newWorkspace;
    }

    /// <summary>need09-01：AddWorkspace 后自动建 Project 空域；失败只 Warn。容忍 .haloagent 不存在视为空域。</summary>
    private static void TryEnsureProjectSkillMemory(string normalizedPath)
    {
        try
        {
            SkillMemoryService.EnsureProjectInitializedStatic(normalizedPath);
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Warn($"[Workspace] .haloagent 初始化失败已忽略：{normalizedPath}", ex);
            }
            catch
            { /* 日志失败也不阻塞 */
            }
        }
    }

    /// <summary>
    /// need09-01：移除工作区仅解绑 + 可选删会话物理目录；.haloagent/ 归项目所有，随项目文件夹走，永不删除。
    /// </summary>
    public async Task<bool> RemoveWorkspaceAsync(
        string workspaceId,
        bool deletePhysicalSessionFolder = true,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;
        var workspaces = await _sessionRepository
            .LoadWorkspacesIndexAsync(ct)
            .ConfigureAwait(false);
        int removed = workspaces.RemoveAll(w =>
            string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
        );
        if (removed > 0)
        {
            await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);
            if (deletePhysicalSessionFolder)
            {
                // 注意：仅删数据根下的会话物理目录，不碰工作区 RootPath 下的 .haloagent/。
                await _sessionRepository
                    .DeleteWorkspaceDirectoryAsync(workspaceId, ct)
                    .ConfigureAwait(false);
            }
            return true;
        }
        return false;
    }

    public async Task<bool> SetWorkspacePreferenceAsync(
        string workspaceId,
        string? providerId,
        string? modelId,
        CancellationToken ct = default
    )
    {
        var workspaces = await _sessionRepository
            .LoadWorkspacesIndexAsync(ct)
            .ConfigureAwait(false);
        var ws = workspaces.FirstOrDefault(w =>
            string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
        );
        if (ws == null)
            return false;

        ws.DefaultProviderId = providerId;
        ws.DefaultModelId = modelId;
        await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TouchWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspaces = await _sessionRepository
            .LoadWorkspacesIndexAsync(ct)
            .ConfigureAwait(false);
        var ws = workspaces.FirstOrDefault(w =>
            string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
        );
        if (ws == null)
            return false;

        ws.LastOpenedAtUtc = DateTime.UtcNow;
        await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<List<SessionMetadata>> GetSessionsByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return new List<SessionMetadata>();
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        return sessions
            .Where(s => !string.Equals(s.AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.UpdatedAtUtc)
            .ToList();
    }

    public Task<SessionMetadata> CreateSessionAsync(
        string workspaceId,
        string? title = null,
        string? providerId = null,
        string? modelId = null,
        CancellationToken ct = default
    ) =>
        CreateSubSessionAsync(
            workspaceId,
            title,
            providerId,
            modelId,
            parentSessionId: null,
            agentRole: "main",
            subLabel: null,
            createdByCallId: null,
            taskPrompt: null,
            ct
        );

    /// <summary>need13：建子会话（扁平寄宿 sessions/&lt;ws&gt;/&lt;subId&gt;；父子只靠 index ParentSessionId）。</summary>
    public async Task<SessionMetadata> CreateSubSessionAsync(
        string workspaceId,
        string? title,
        string? providerId,
        string? modelId,
        string? parentSessionId,
        string agentRole,
        string? subLabel,
        string? createdByCallId,
        string? taskPrompt,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("Workspace ID cannot be empty.", nameof(workspaceId));
        }

        string role = string.Equals(agentRole, "sub", StringComparison.OrdinalIgnoreCase)
            ? "sub"
            : "main";
        bool isSub = string.Equals(role, "sub", StringComparison.Ordinal);
        string sessionId = $"sess_{DateTime.UtcNow:yyyyMMdd}_{Guid.NewGuid():N}"[..22];
        string sessionHash = HeaderVariableResolver.ComputeDeterministicHash16(
            $"{workspaceId}:{sessionId}"
        );

        string label = (subLabel ?? string.Empty).Trim();
        if (label.Length > 20)
            label = label[..20];
        string task = taskPrompt ?? string.Empty;
        string taskIdx = task.Length > 2000 ? task[..2000] : task;
        string resolvedTitle;
        if (isSub)
        {
            string seed = !string.IsNullOrWhiteSpace(label)
                ? label
                : (task.Length > 12 ? task[..12] : task);
            seed = seed.Trim();
            if (string.IsNullOrWhiteSpace(seed))
                seed = sessionId;
            resolvedTitle = "sub: " + seed;
        }
        else
        {
            resolvedTitle = string.IsNullOrWhiteSpace(title) ? "新建会话" : title.Trim();
        }

        var meta = new SessionMetadata
        {
            Id = sessionId,
            WorkspaceId = workspaceId,
            SessionHash = sessionHash,
            Title = resolvedTitle,
            ProviderId = providerId,
            ModelId = modelId,
            MessageCount = 0,
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
            TotalCachedTokens = 0,
            IsPinned = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            ParentSessionId = isSub ? parentSessionId : null,
            AgentRole = role,
            SubLabel = isSub ? (string.IsNullOrWhiteSpace(label) ? null : label) : null,
            SubStatus = isSub ? "pending" : null,
            CreatedByCallId = isSub ? createdByCallId : null,
            TaskPrompt = isSub ? (string.IsNullOrEmpty(taskIdx) ? null : taskIdx) : null,
        };
        if (isSub)
            meta.TitleLocked = true;

        // 1. 保存 meta.json
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        // 2. 更新 sessions-index.json
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        sessions.Insert(0, meta);
        await _sessionRepository
            .SaveSessionsIndexAsync(workspaceId, sessions, ct)
            .ConfigureAwait(false);

        try
        {
            Infrastructure.Logging.HaloLogger.App.Info(
                $"[SubAgent] created sub={sessionId} parent={parentSessionId} label={label} call={createdByCallId}"
            );
        }
        catch { }
        return meta;
    }

    /// <summary>need13：查主下全部子（创建时间正序）。</summary>
    public async Task<List<SessionMetadata>> GetSubSessionsAsync(
        string workspaceId,
        string parentSessionId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(parentSessionId))
            return new List<SessionMetadata>();
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        return sessions
            .Where(s =>
                string.Equals(
                    s.ParentSessionId,
                    parentSessionId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .OrderBy(s => s.CreatedAtUtc)
            .ToList();
    }

    public async Task<SessionMetadata?> GetSubSessionAsync(
        string workspaceId,
        string parentSessionId,
        string subSessionId,
        CancellationToken ct = default
    )
    {
        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, subSessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
            return null;
        if (
            !string.Equals(
                meta.ParentSessionId,
                parentSessionId,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return null;
        return meta;
    }

    /// <summary>need13：级联删树（先删子盘再删主盘；取消由 Bridge 层先调 CancelTree）。</summary>
    public async Task<bool> DeleteSessionTreeAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            var subs = await GetSubSessionsAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
            foreach (var sub in subs)
            {
                try
                {
                    await _sessionRepository
                        .DeleteSessionAsync(workspaceId, sub.Id, ct)
                        .ConfigureAwait(false);
                }
                catch { }
            }
            bool ok = await _sessionRepository
                .DeleteSessionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[SubAgent] delete-tree parent={sessionId} subs={subs.Count}"
                );
            }
            catch { }
            return ok;
        }
        catch
        {
            return await _sessionRepository
                .DeleteSessionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
        }
    }

    public Task<SessionMetadata?> GetSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.LoadSessionMetaAsync(workspaceId, sessionId, ct);
    }

    public Task<List<UnifiedChatMessage>> GetSessionMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.GetMessagesAsync(workspaceId, sessionId, ct);
    }

    public async Task<bool> AppendSessionMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    )
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var appendLock = _sessionAppendLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. 追加写至 messages.jsonl
            await _sessionRepository
                .AppendMessageAsync(workspaceId, sessionId, message, ct)
                .ConfigureAwait(false);

            // 2. 更新会话元数据
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
            {
                meta = new SessionMetadata
                {
                    Id = sessionId,
                    WorkspaceId = workspaceId,
                    SessionHash = HeaderVariableResolver.ComputeDeterministicHash16(
                        $"{workspaceId}:{sessionId}"
                    ),
                    CreatedAtUtc = DateTime.UtcNow
                };
            }

            meta.MessageCount++;
            // need04-05：IN/缓存快照语义（有精确值则覆盖），OUT 为增量可加
            if (message.InputTokens.HasValue && message.InputTokens.Value > 0)
                meta.TotalInputTokens = message.InputTokens.Value;
            else
                meta.TotalInputTokens += TokenBudgetService.EstimateMessageTokens(message);
            if (message.OutputTokens.HasValue)
                meta.TotalOutputTokens += message.OutputTokens.Value;
            if (message.CachedTokens.HasValue && message.CachedTokens.Value > 0)
                meta.TotalCachedTokens = message.CachedTokens.Value;
            // need05-01：Σ账单累计（每次 usage 到账累加；user/tool 估算分支不碰 Cumulative；compact 重写保留，clear 清零）
            if (message.InputTokens.HasValue && message.InputTokens.Value > 0)
                meta.CumulativePromptTokens += message.InputTokens.Value;
            if (message.OutputTokens.HasValue && message.OutputTokens.Value > 0)
                meta.CumulativeCompletionTokens += message.OutputTokens.Value;
            meta.UpdatedAtUtc = DateTime.UtcNow;

            // need03-03 T1：首条 user 且未冻结时取第一句话前 5 字（与前端 instantTitleFromUserText 同语义，surrogate 安全）
            // need13：子会话标题已锁定（sub: label），跳过自动精炼
            if (
                !string.Equals(meta.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !meta.TitleLocked
                && (
                    string.IsNullOrWhiteSpace(meta.Title)
                    || meta.Title == "新建会话"
                    || meta.Title == "初始会话"
                )
            )
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    var trimmed = message.Content.Trim();
                    var firstLine =
                        trimmed
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? trimmed;
                    var firstSentence =
                        firstLine
                            .Split(
                                new[] { '。', '！', '？', '!', '?', ';', '；' },
                                StringSplitOptions.RemoveEmptyEntries
                            )
                            .FirstOrDefault()
                            ?.Trim() ?? firstLine.Trim();
                    var compact = new string(
                        firstSentence.Where(c => !char.IsWhiteSpace(c)).ToArray()
                    );
                    var elements = System.Globalization.StringInfo.ParseCombiningCharacters(
                        compact
                    );
                    int take = Math.Min(5, elements.Length);
                    var candidate =
                        take <= 0
                            ? string.Empty
                            : compact.Substring(
                                elements[0],
                                (take == elements.Length ? compact.Length : elements[take])
                                    - elements[0]
                            );
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        meta.Title = candidate;
                    }
                }
            }

            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);

            return true;
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>
    /// need05-03：Append 后半段“存 meta+刷 index”抽取，供压缩重写复用（纯搬代码，不改语义）。
    /// 注意：调用方须已持有会话级互斥（Append 持 _sessionAppendLocks[sessionId]，压缩持同一把锁）。
    /// </summary>
    internal async Task RefreshMetaIndexAsync(
        string workspaceId,
        string sessionId,
        SessionMetadata meta,
        CancellationToken ct = default
    )
    {
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        // 同步更新 sessions-index.json
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx] = meta;
        }
        else
        {
            sessions.Insert(0, meta);
        }
        await _sessionRepository
            .SaveSessionsIndexAsync(workspaceId, sessions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>need05-03：供压缩服务持有追加锁（与 AppendSessionMessageAsync 同一把锁，天然互斥）。</summary>
    internal SemaphoreSlim GetSessionAppendLock(string sessionId) =>
        _sessionAppendLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public async Task<bool> UpdateSessionTitleAsync(
        string workspaceId,
        string sessionId,
        string newTitle,
        CancellationToken ct = default
    )
    {
        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
            return false;
        meta.Title = string.IsNullOrWhiteSpace(newTitle) ? "未命名会话" : newTitle.Trim();
        // need03-03：任何显式改名（含前端 LLM 精炼回写）即永久冻结，此后 Append 永不覆盖
        meta.TitleLocked = true;
        meta.UpdatedAtUtc = DateTime.UtcNow;
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx].Title = meta.Title;
            sessions[idx].TitleLocked = true;
            sessions[idx].UpdatedAtUtc = meta.UpdatedAtUtc;
            await _sessionRepository
                .SaveSessionsIndexAsync(workspaceId, sessions, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> SetSessionPinnedAsync(
        string workspaceId,
        string sessionId,
        bool isPinned,
        CancellationToken ct = default
    )
    {
        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
            return false;

        meta.IsPinned = isPinned;
        meta.UpdatedAtUtc = DateTime.UtcNow;
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx].IsPinned = isPinned;
            sessions[idx].UpdatedAtUtc = meta.UpdatedAtUtc;
            await _sessionRepository
                .SaveSessionsIndexAsync(workspaceId, sessions, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    public Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.DeleteSessionAsync(workspaceId, sessionId, ct);
    }

    public Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.ExportSessionMarkdownAsync(workspaceId, sessionId, ct);
    }

    /// <summary>need07：重放截断。持追加锁；切面以后端为准；失败不动盘。</summary>
    public async Task<HistoryTruncateResult> TruncateSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        bool removeAnchor,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryTruncateResult.Fail("SESSION_NOT_FOUND");
        if (string.IsNullOrWhiteSpace(anchorMessageId))
            return HistoryTruncateResult.Fail("ANCHOR_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryTruncateResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            int idx = messages.FindIndex(m =>
                string.Equals(m.Id, anchorMessageId, StringComparison.OrdinalIgnoreCase)
            );
            if (idx < 0)
                return HistoryTruncateResult.Fail("ANCHOR_NOT_FOUND");
            string role = (messages[idx].Role ?? string.Empty).ToLowerInvariant();
            int cut;
            string? replayText;
            if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                int j = idx;
                while (
                    j >= 0
                    && !string.Equals(messages[j].Role, "user", StringComparison.OrdinalIgnoreCase)
                )
                    j--;
                if (j < 0)
                    return HistoryTruncateResult.Fail("NO_PRIOR_USER");
                cut = j;
                replayText = messages[j].Content;
            }
            else if (string.Equals(role, "user", StringComparison.Ordinal))
            {
                cut = idx;
                replayText = messages[idx].Content;
            }
            else
            {
                cut = removeAnchor ? idx : idx + 1;
                replayText = messages[idx].Content;
            }
            if (string.IsNullOrWhiteSpace(replayText))
                return HistoryTruncateResult.Fail("EMPTY_REPLAY_TEXT");
            var keptRaw = messages.Take(cut).ToList();
            int removed = messages.Count - keptRaw.Count;
            // FIX(tool-400)：截断切面可能劈开 assistant→tool 块，落盘前内存愈合，保证尾部配对完整。
            var (kept, synthetic, orphans) = RebuildHealedHistory(keptRaw);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.replay-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, kept, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryTruncateResult.Fail("REWRITE_ERROR");
            }
            meta.MessageCount = kept.Count;
            var lastSnap = kept.LastOrDefault(m =>
                m.InputTokens.HasValue && m.InputTokens.Value > 0
            );
            meta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            meta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] truncate ws={workspaceId} sess={sessionId} anchor={anchorMessageId} kept={kept.Count} removed={removed} synthetic={synthetic} orphans={orphans} bak={bak}"
                );
            }
            catch { }
            return HistoryTruncateResult.Ok(kept.Count, removed, replayText, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>need07：分支。前缀复制建新会话，原会话不动。</summary>
    public async Task<SessionMetadata> BranchSessionAsync(
        string workspaceId,
        string sourceSessionId,
        string anchorMessageId,
        bool includeAnchor,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sourceSessionId))
            throw new ArgumentException("Workspace ID 与源会话 ID 不能为空");
        var srcMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sourceSessionId, ct)
            .ConfigureAwait(false);
        if (srcMeta == null)
            throw new InvalidOperationException("SESSION_NOT_FOUND");
        var messages = await _sessionRepository
            .GetMessagesAsync(workspaceId, sourceSessionId, ct)
            .ConfigureAwait(false);
        int idx = messages.FindIndex(m =>
            string.Equals(m.Id, anchorMessageId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx < 0)
            throw new InvalidOperationException("ANCHOR_NOT_FOUND");
        int end = idx;
        if (string.Equals(messages[idx].Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            int t = idx + 1;
            while (
                t < messages.Count
                && string.Equals(messages[t].Role, "tool", StringComparison.OrdinalIgnoreCase)
            )
                t++;
            end = t - 1;
        }
        var prefixRaw = (includeAnchor ? messages.Take(end + 1) : messages.Take(idx)).ToList();
        // FIX(tool-400)：分支前缀同样可能劈开 assistant→tool 块，复制前内存愈合。
        var (prefix, _, _) = RebuildHealedHistory(prefixRaw);
        string title =
            string.IsNullOrWhiteSpace(srcMeta.Title) || srcMeta.Title == "新建会话"
                ? "新建会话"
                : srcMeta.Title + "·分支";
        // need13：分支永远是新 main，不继承父子链（CreateSessionAsync 委托 parent=null 即 main）
        var created = await CreateSessionAsync(
                workspaceId,
                title,
                srcMeta.ProviderId,
                srcMeta.ModelId,
                ct
            )
            .ConfigureAwait(false);
        var cloned = prefix
            .Select(m => new UnifiedChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ReasoningContent = m.ReasoningContent,
                ToolCalls = m.ToolCalls != null ? new List<UnifiedToolCall>(m.ToolCalls) : null,
                ToolCallId = m.ToolCallId,
                Name = m.Name,
                InputTokens = m.InputTokens,
                OutputTokens = m.OutputTokens,
                CachedTokens = m.CachedTokens,
                Images = m.Images != null ? new List<ChatImageRef>(m.Images) : null,
            })
            .ToList();
        await _sessionRepository
            .RewriteMessagesJsonlAsync(workspaceId, created.Id, cloned, ct)
            .ConfigureAwait(false);
        var newMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, created.Id, ct)
            .ConfigureAwait(false);
        if (newMeta != null)
        {
            newMeta.MessageCount = cloned.Count;
            var lastSnap = cloned.LastOrDefault(m =>
                m.InputTokens.HasValue && m.InputTokens.Value > 0
            );
            newMeta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            newMeta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            newMeta.CumulativePromptTokens = 0;
            newMeta.CumulativeCompletionTokens = 0;
            newMeta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, created.Id, newMeta, ct).ConfigureAwait(false);
            try
            {
                string dir = _sessionRepository.GetSessionDirectory(workspaceId, created.Id);
                System.IO.Directory.CreateDirectory(dir);
                string shortSrc =
                    sourceSessionId.Length > 8 ? sourceSessionId.Substring(0, 8) : sourceSessionId;
                string infoPath = System.IO.Path.Combine(
                    dir,
                    "messages.branch-src-" + shortSrc + ".json"
                );
                string infoJson = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        sourceSessionId,
                        anchorMessageId,
                        copiedAtUtc = DateTime.UtcNow,
                        copiedMessages = cloned.Count
                    }
                );
                await System
                    .IO.File.WriteAllTextAsync(infoPath, infoJson, ct)
                    .ConfigureAwait(false);
            }
            catch { }
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] branch ws={workspaceId} src={sourceSessionId} anchor={anchorMessageId} new={created.Id} copied={cloned.Count}"
                );
            }
            catch { }
            return newMeta;
        }
        return created;
    }

    /// <summary>synthetic cancelled tool 回包前缀（前端/导出可据此识别为中断补位，非真实工具输出）。</summary>
    public const string SyntheticCancelledToolPrefix = "[Tool Cancelled";

    public static string SyntheticCancelledToolText(string? functionName)
    {
        string name = string.IsNullOrWhiteSpace(functionName) ? "tool" : functionName!.Trim();
        return $"[Tool Cancelled - {name}]: 该工具调用在实际执行前被中断（用户手动停止或进程异常退出），未产生任何副作用。请基于已有上下文继续任务；如仍需要该操作，请重新发起工具调用。";
    }

    /// <summary>
    /// 纯函数：重建愈合后的历史。缺回包的 assistant tool_call 在其后原地补 synthetic cancelled tool；
    /// 找不到归属 assistant 的 tool 消息直接丢弃（否则下轮发模型必 400）。
    /// </summary>
    internal static (
        List<UnifiedChatMessage> Fixed,
        int Synthetic,
        int RemovedOrphans
    ) RebuildHealedHistory(IReadOnlyList<UnifiedChatMessage> messages)
    {
        var uncoveredByAssistant = new Dictionary<int, List<(string CallId, string? Name)>>();
        var orphanToolIndexes = new HashSet<int>();
        int n = messages?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            var m = messages![i];
            string role = (m.Role ?? string.Empty).ToLowerInvariant();
            if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                var calls = m
                    .ToolCalls?.Where(c => c != null && !string.IsNullOrWhiteSpace(c.CallId))
                    .ToList();
                if (calls == null || calls.Count == 0)
                    continue;
                var covered = new HashSet<string>(StringComparer.Ordinal);
                for (int j = i + 1; j < n; j++)
                {
                    var t = messages[j];
                    if (
                        !string.Equals(
                            (t.Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            StringComparison.Ordinal
                        )
                    )
                        break;
                    if (!string.IsNullOrWhiteSpace(t.ToolCallId))
                        covered.Add(t.ToolCallId!);
                }
                var uncovered = calls
                    .Where(c => !covered.Contains(c.CallId))
                    .Select(c => (c.CallId, (string?)c.FunctionName))
                    .ToList();
                if (uncovered.Count > 0)
                    uncoveredByAssistant[i] = uncovered;
            }
            else if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                // 回溯连续 tool 段找归属 assistant
                bool owned = false;
                if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                {
                    int k = i - 1;
                    while (
                        k >= 0
                        && string.Equals(
                            (messages[k].Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            StringComparison.Ordinal
                        )
                    )
                        k--;
                    if (
                        k >= 0
                        && string.Equals(
                            (messages[k].Role ?? string.Empty).ToLowerInvariant(),
                            "assistant",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        owned =
                            messages[k]
                                .ToolCalls?.Any(c =>
                                    c != null
                                    && string.Equals(
                                        c.CallId,
                                        m.ToolCallId,
                                        StringComparison.Ordinal
                                    )
                                ) == true;
                    }
                }
                if (!owned)
                    orphanToolIndexes.Add(i);
            }
        }
        if (uncoveredByAssistant.Count == 0 && orphanToolIndexes.Count == 0)
        {
            return (messages!.ToList(), 0, 0);
        }
        // FIX(tool-400)：合成一律追加在存量 tool 块尾（[assistant, toolA, syntheticB]），不插在块中，避免严格网关误杀。
        var fixed_ = new List<UnifiedChatMessage>(
            n + uncoveredByAssistant.Values.Sum(v => v.Count)
        );
        int synthetic = 0;
        int idx = 0;
        while (idx < n)
        {
            if (orphanToolIndexes.Contains(idx))
            {
                idx++;
                continue;
            }
            fixed_.Add(messages![idx]);
            if (uncoveredByAssistant.TryGetValue(idx, out var uncovered))
            {
                int j = idx + 1;
                while (
                    j < n
                    && string.Equals(
                        (messages[j].Role ?? string.Empty).ToLowerInvariant(),
                        "tool",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    if (orphanToolIndexes.Contains(j))
                    {
                        j++;
                        continue;
                    }
                    fixed_.Add(messages[j]);
                    j++;
                }
                foreach (var (callId, name) in uncovered)
                {
                    fixed_.Add(
                        UnifiedChatMessage.Tool(callId, SyntheticCancelledToolText(name), name)
                    );
                    synthetic++;
                }
                idx = j;
            }
            else
            {
                idx++;
            }
        }
        return (fixed_, synthetic, orphanToolIndexes.Count);
    }

    /// <summary>孤儿愈合落盘（含备份 + meta 计数修正）。无损伤时只读不写。</summary>
    public async Task<HistoryHealResult> HealSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryHealResult.Fail("SESSION_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryHealResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            var (fixed_, synthetic, removed) = RebuildHealedHistory(messages);
            if (synthetic == 0 && removed == 0)
                return HistoryHealResult.Clean(messages.Count);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.heal-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryHealResult.Fail("REWRITE_ERROR");
            }
            meta.MessageCount = fixed_.Count;
            var lastSnap = fixed_.LastOrDefault(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.InputTokens.HasValue
                && m.InputTokens.Value > 0
            );
            meta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            meta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] heal ws={workspaceId} sess={sessionId} synthetic={synthetic} removedOrphans={removed} kept={fixed_.Count} bak={bak}"
                );
            }
            catch { }
            return HistoryHealResult.Ok(synthetic, removed, fixed_.Count, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>删除单条消息（先备份 → 删行 → 同一次重写内愈合，失败不动盘）。</summary>
    public async Task<HistoryDeleteResult> DeleteSessionMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryDeleteResult.Fail("SESSION_NOT_FOUND");
        if (string.IsNullOrWhiteSpace(messageId))
            return HistoryDeleteResult.Fail("MESSAGE_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryDeleteResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            int idx = messages.FindIndex(m =>
                string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase)
            );
            if (idx < 0)
                return HistoryDeleteResult.Fail("MESSAGE_NOT_FOUND");
            var kept = messages.Where((_, i) => i != idx).ToList();
            var (fixed_, synthetic, removed) = RebuildHealedHistory(kept);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.delete-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryDeleteResult.Fail("REWRITE_ERROR");
            }
            meta.MessageCount = fixed_.Count;
            var lastSnap = fixed_.LastOrDefault(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.InputTokens.HasValue
                && m.InputTokens.Value > 0
            );
            meta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            meta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] delete ws={workspaceId} sess={sessionId} msg={messageId} kept={fixed_.Count} synthetic={synthetic} removedOrphans={removed} bak={bak}"
                );
            }
            catch { }
            return HistoryDeleteResult.Ok(fixed_.Count, synthetic, removed, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }
}
