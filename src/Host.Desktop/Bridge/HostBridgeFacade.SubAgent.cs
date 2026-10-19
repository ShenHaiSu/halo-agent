using Host.Desktop.Core.Models;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>need13-05：子代理运行时 DTO（供诊断卡 E 卡）。need16 扩展 RecentRejections。</summary>
public sealed class SubRuntimeInfo
{
    public List<SubRuntimeItem> Subs { get; set; } = new();
    public SubPolicyDto Policy { get; set; } = new();
    public SubRejectionInfo? LastRejection { get; set; }
    public List<SubRejectionInfo> RecentRejections { get; set; } = new();
}

public sealed class SubRuntimeItem
{
    public string SubId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Iteration { get; set; }
    public string Tool { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
}

public sealed class SubPolicyDto
{
    public bool Enabled { get; set; } = true;
    public int MaxParallel { get; set; } = 3;
    public int MaxIterations { get; set; } = 15;
    public bool Exec { get; set; } = true;
    public bool File { get; set; } = true;
    public bool Skill { get; set; } = false;
    public bool Memory { get; set; } = false;
}

public sealed class SubRejectionInfo
{
    public string Code { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string CallId { get; set; } = string.Empty;
}

/// <summary>need13-05：子代理桥接（GetSubSessions/CancelSubAgent/CancelTree/GetSubRuntime）。need16 扩展详情接口。</summary>
public sealed partial class HostBridgeFacade
{
    public async Task<BridgeResponse<List<SessionMetadata>>> GetSubSessions(string workspaceId, string parentSessionId)
    {
        try
        {
            var list = await _workspaceSessionService.GetSubSessionsAsync(workspaceId, parentSessionId).ConfigureAwait(false);
            return BridgeResponse<List<SessionMetadata>>.Success(list);
        }
        catch (Exception ex)
        {
            return BridgeResponse<List<SessionMetadata>>.Fail("SUB_LIST_ERROR", ex.Message);
        }
    }

    public Task<BridgeResponse<bool>> CancelSubAgent(string workspaceId, string subSessionId)
    {
        try
        {
            bool ok = false;
            try
            {
                var meta = _workspaceSessionService.GetSessionAsync(workspaceId, subSessionId).GetAwaiter().GetResult();
                string? parent = meta?.ParentSessionId;
                if (!string.IsNullOrWhiteSpace(parent))
                    ok = SubCoordinator.CancelSub(parent!, subSessionId.Trim());
            }
            catch { }
            if (ok) return Task.FromResult(BridgeResponse<bool>.Success(true, "已发送子取消指令（主继续等待其 Cancelled 结果）"));
            return Task.FromResult(BridgeResponse<bool>.Success(false, "该子代理未在运行"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_ERROR", ex.Message));
        }
    }

    public async Task<BridgeResponse<SubRuntimeInfo>> GetSubRuntime(string workspaceId, string parentSessionId)
    {
        try
        {
            var cfg = _agentConfigService.GetInternal();
            var info = new SubRuntimeInfo
            {
                Policy = new SubPolicyDto
                {
                    Enabled = cfg.SubAgentEnabled,
                    MaxParallel = cfg.SubMaxParallel,
                    MaxIterations = cfg.SubMaxIterations,
                    Exec = cfg.SubAllowExecCommand,
                    File = cfg.SubAllowFileWrite,
                    Skill = cfg.SubAllowSkillWrite,
                    Memory = cfg.SubAllowMemoryWrite,
                },
            };
            var active = SubCoordinator.GetActive(parentSessionId);
            var subs = await _workspaceSessionService.GetSubSessionsAsync(workspaceId, parentSessionId).ConfigureAwait(false);
            var metaById = subs.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
            foreach (var h in active)
            {
                metaById.TryGetValue(h.SubSessionId, out var meta);
                info.Subs.Add(new SubRuntimeItem
                {
                    SubId = h.SubSessionId,
                    Label = h.Label,
                    Status = meta?.SubStatus ?? "running",
                    Iteration = 0,
                    Tool = string.Empty,
                    ElapsedMs = (long)(DateTime.UtcNow - h.StartedAtUtc).TotalMilliseconds,
                });
            }
            // need16：兼容老 LastRejection + 新 RecentRejections（内存为空即空列表，老前端忽略未知字段）
            info.RecentRejections = info.LastRejection != null ? new List<SubRejectionInfo> { info.LastRejection } : new List<SubRejectionInfo>();
            return BridgeResponse<SubRuntimeInfo>.Success(info);
        }
        catch (Exception ex)
        {
            return BridgeResponse<SubRuntimeInfo>.Fail("SUB_RUNTIME_ERROR", ex.Message);
        }
    }

    public static bool IsSubReadOnly(SessionMetadata? meta)
        => meta != null && string.Equals(meta.AgentRole, "sub", StringComparison.OrdinalIgnoreCase);

    public async Task<BridgeResponse<SubSessionDetailDto>> GetSubSessionDetail(string workspaceId, string subSessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(subSessionId))
                return BridgeResponse<SubSessionDetailDto>.Fail("SUB_NOT_FOUND", "子会话不存在");
            var meta = await _workspaceSessionService.GetSessionAsync(workspaceId, subSessionId).ConfigureAwait(false);
            if (meta == null)
                return BridgeResponse<SubSessionDetailDto>.Fail("SUB_NOT_FOUND", "子会话不存在");
            if (string.IsNullOrWhiteSpace(meta.ParentSessionId))
                return BridgeResponse<SubSessionDetailDto>.Fail("NOT_A_SUB", "该会话不是子会话");
            string parent = meta.ParentSessionId!.Trim();
            var active = SubCoordinator.GetActive(parent);
            bool isRunning = active.Any(h => string.Equals(h.SubSessionId, subSessionId, StringComparison.OrdinalIgnoreCase));
            var (inp, outp, cached, msgs, tools, iters) = await SubStatsReader.ReadStatsAsync(_workspaceSessionService, workspaceId, subSessionId).ConfigureAwait(false);
            var dto = new SubSessionDetailDto
            {
                SubId = meta.Id,
                ParentSessionId = parent,
                Label = meta.SubLabel ?? string.Empty,
                Status = isRunning ? "running" : (meta.SubStatus ?? "unknown"),
                TaskPrompt = meta.TaskPrompt,
                CreatedByCallId = meta.CreatedByCallId,
                MessageCount = msgs,
                Iterations = iters,
                ToolExecutions = tools,
                InputTokens = inp,
                OutputTokens = outp,
                CachedTokens = cached,
                BilledPrompt = meta.CumulativePromptTokens,
                BilledCompletion = meta.CumulativeCompletionTokens,
                CreatedAtUtc = meta.CreatedAtUtc,
                UpdatedAtUtc = meta.UpdatedAtUtc,
            };
            // failed 原因行：读尾部 tool/assistant 错误文本（尽力，不抛）
            try
            {
                var messages = await _workspaceSessionService.GetSessionMessagesAsync(workspaceId, subSessionId).ConfigureAwait(false);
                var lastErr = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content) && (m.Content!.Contains("失败") || m.Content.Contains("failed", StringComparison.OrdinalIgnoreCase) || m.Content.Contains("错误")));
                if (lastErr != null && !string.Equals(dto.Status, "running", StringComparison.OrdinalIgnoreCase) && !string.Equals(dto.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    dto.ErrorMessage = lastErr.Content?.Length > 500 ? lastErr.Content[..500] : lastErr.Content;
                    dto.ErrorCode = dto.Status;
                }
            }
            catch { }
            return BridgeResponse<SubSessionDetailDto>.Success(dto);
        }
        catch (Exception ex)
        {
            return BridgeResponse<SubSessionDetailDto>.Fail("SUB_LIST_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<List<UnifiedChatMessage>>> GetSubSessionMessages(string workspaceId, string subSessionId, int? limit = null, string? beforeMessageId = null)
    {
        try
        {
            var meta = await _workspaceSessionService.GetSessionAsync(workspaceId, subSessionId).ConfigureAwait(false);
            if (meta == null)
                return BridgeResponse<List<UnifiedChatMessage>>.Fail("SUB_NOT_FOUND", "子会话不存在");
            if (string.IsNullOrWhiteSpace(meta.ParentSessionId))
                return BridgeResponse<List<UnifiedChatMessage>>.Fail("NOT_A_SUB", "该会话不是子会话");
            var messages = await _workspaceSessionService.GetSessionMessagesAsync(workspaceId, subSessionId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(beforeMessageId))
            {
                int idx = messages.FindIndex(m => string.Equals(m.Id, beforeMessageId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) messages = messages.Take(idx).ToList();
            }
            if (limit.HasValue && limit.Value > 0 && messages.Count > limit.Value)
                messages = messages.Skip(messages.Count - limit.Value).ToList();
            return BridgeResponse<List<UnifiedChatMessage>>.Success(messages);
        }
        catch (Exception ex)
        {
            return BridgeResponse<List<UnifiedChatMessage>>.Fail("SUB_LIST_ERROR", ex.Message);
        }
    }
}
