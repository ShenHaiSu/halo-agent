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

/// <summary>
/// 工作区与会话领域服务实现
/// </summary>
public sealed partial class WorkspaceSessionService : IWorkspaceSessionService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly SessionOperationCoordinator _operationCoordinator;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionAppendLocks = new();

    public WorkspaceSessionService(
        ISessionRepository sessionRepository,
        SessionOperationCoordinator? operationCoordinator = null
    )
    {
        _sessionRepository =
            sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _operationCoordinator = operationCoordinator ?? SessionOperationCoordinator.Shared;
    }

    internal SessionOperationCoordinator OperationCoordinator => _operationCoordinator;

    /// <summary>直接调用领域服务时也必须经过统一协调器，不能旁路。</summary>
    internal SessionOperationLease AcquireHistoryLease(string workspaceId, string sessionId)
    {
        if (
            !_operationCoordinator.TryAcquire(
                workspaceId,
                sessionId,
                SessionOperationKind.History,
                out var lease
            )
        )
            throw new InvalidOperationException("HISTORY_OPERATION_IN_PROGRESS");
        return lease!;
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

    /// <summary>别名长度上限（界面单行展示用；超长截断而非报错）。</summary>
    private const int MaxWorkspaceAliasLength = 40;

    /// <summary>
    /// 设置/清除工作区别名。只写 Alias 一个字段；Name（提示词/请求头数据源）保持不动。
    /// </summary>
    public async Task<WorkspaceInfo?> SetWorkspaceAliasAsync(
        string workspaceId,
        string? alias,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        var workspaces = await _sessionRepository
            .LoadWorkspacesIndexAsync(ct)
            .ConfigureAwait(false);
        var ws = workspaces.FirstOrDefault(w =>
            string.Equals(w.Id, workspaceId.Trim(), StringComparison.OrdinalIgnoreCase)
        );
        if (ws == null)
            return null;

        string trimmed = (alias ?? string.Empty).Trim();
        if (trimmed.Length > MaxWorkspaceAliasLength)
            trimmed = trimmed[..MaxWorkspaceAliasLength];

        // 空白 = 清除别名（回退显示 Name）；同时清理历史误存的空串
        ws.Alias = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        await _sessionRepository.SaveWorkspacesIndexAsync(workspaces, ct).ConfigureAwait(false);
        return ws;
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
            StorageFormatVersion = WorkspaceHistorySchema.CurrentFormatVersion,
            SessionKind = isSub ? "sub" : "main",
        };
        if (isSub)
            meta.TitleLocked = true;

        // 1. 保存 meta.json（元数据文件为权威源，先落盘再同步注册表）
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        // 2. 更新 sessions-index.json
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        sessions.Insert(0, meta);
        await _sessionRepository
            .SaveSessionsIndexAsync(workspaceId, sessions, ct)
            .ConfigureAwait(false);

        // 3. 同步工作区库 sessions 注册表（派生缓存，不符以 meta.json 覆盖）
        try
        {
            var registry = new SessionHistoryStore(
                new SessionHistoryDb(_sessionRepository.Storage)
            );
            await registry
                .RegisterSessionAsync(
                    workspaceId,
                    sessionId,
                    WorkspaceHistorySchema.CurrentFormatVersion,
                    meta.SessionKind,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                Infrastructure.Logging.HaloLogger.App.Warn(
                    "[Workspace] registry sync failed sess=" + sessionId + ": " + ex.Message
                );
            }
            catch { }
        }

        try
        {
            Infrastructure.Logging.HaloLogger.App.Info(
                $"[SubAgent] created sub={sessionId} parent={parentSessionId} label={label} call={createdByCallId}"
            );
        }
        catch { }
        return meta;
    }

    /// <summary>工作区库注册表启动对账：逐目录读元数据文件补齐派生缓存，老会话不进表，三业务表只读不写；仅统计真实修复数。</summary>
    public async Task<int> ReconcileWorkspaceRegistryAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return 0;
        }
        int fixedCount = 0;
        try
        {
            string wsDir = Path.Combine(
                _sessionRepository.Storage.SessionsDirectory,
                workspaceId.Trim()
            );
            if (!Directory.Exists(wsDir))
            {
                return 0;
            }
            var db = new SessionHistoryDb(_sessionRepository.Storage);
            try
            {
                db.EnsureWorkspaceDatabase(workspaceId);
            }
            catch { }
            var registry = new SessionHistoryStore(db);
            string[] sessionDirs;
            try
            {
                sessionDirs = Directory.GetDirectories(wsDir);
            }
            catch
            {
                return 0;
            }
            foreach (string dir in sessionDirs)
            {
                ct.ThrowIfCancellationRequested();
                string sessionId = Path.GetFileName(
                    dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                );
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    continue;
                }
                SessionMetadata? meta;
                try
                {
                    meta = await _sessionRepository
                        .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                if (meta == null || string.IsNullOrWhiteSpace(meta.Id))
                {
                    continue;
                }
                if (meta.StorageFormatVersion != WorkspaceHistorySchema.CurrentFormatVersion)
                {
                    continue;
                }
                string kind = string.Equals(
                    meta.SessionKind,
                    "sub",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? "sub"
                    : string.Equals(
                        meta.SessionKind,
                        "archived",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? "archived"
                        : "main";
                bool needsFix = true;
                try
                {
                    using var conn = db.OpenWorkspace(workspaceId);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT storage_format, session_kind FROM sessions WHERE session_id = $sid;";
                    cmd.Parameters.AddWithValue("$sid", meta.Id);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        int fmt = reader.IsDBNull(0) ? -1 : reader.GetInt32(0);
                        string regKind = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (
                            fmt == WorkspaceHistorySchema.CurrentFormatVersion
                            && string.Equals(regKind, kind, StringComparison.Ordinal)
                        )
                        {
                            needsFix = false;
                        }
                    }
                }
                catch
                {
                    needsFix = true;
                }
                if (!needsFix)
                {
                    continue;
                }
                try
                {
                    await registry
                        .RegisterSessionAsync(
                            workspaceId,
                            meta.Id,
                            WorkspaceHistorySchema.CurrentFormatVersion,
                            kind,
                            ct
                        )
                        .ConfigureAwait(false);
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    try
                    {
                        HaloLogger.App.Warn(
                            "[Workspace] reconcile failed sess=" + meta.Id + ": " + ex.Message
                        );
                    }
                    catch { }
                }
            }
        }
        catch { }
        return fixedCount;
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
}
