using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

public sealed partial class SubAgentCoordinator
{
    /// <summary>need22-P1 T4：Barrier 总体超时预算（子数×单子超时+buffer；单子超时取 turnTimeoutMs）。</summary>
    private TimeSpan ResolveBarrierBudget(int subCount)
    {
        try
        {
            if (_agentConfig != null && subCount > 0)
            {
                var cfg = _agentConfig.GetInternal();
                long single =
                    cfg.TurnTimeoutMs > 0
                        ? cfg.TurnTimeoutMs
                        : Host.Desktop.Core.Models.AgentDefaults.TurnTimeoutMs;
                long buffer = Math.Max(0, cfg.SubBarrierBufferMs);
                long total = checked((long)subCount * single + buffer);
                return TimeSpan.FromMilliseconds(
                    Math.Clamp(
                        total,
                        1000L,
                        (long)Host.Desktop.Core.Models.AgentDefaults.HardTimeoutCeilMs * 8
                    )
                );
            }
        }
        catch
        { /* 配置读取失败回默认预算 */
        }
        return TimeSpan.FromMilliseconds(
            Math.Clamp(
                (long)Math.Max(1, subCount) * Host.Desktop.Core.Models.AgentDefaults.TurnTimeoutMs
                    + Host.Desktop.Core.Models.AgentDefaults.SubBarrierBufferMs,
                1000L,
                (long)Host.Desktop.Core.Models.AgentDefaults.HardTimeoutCeilMs * 8
            )
        );
    }

    private static string TruncateSummary(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Length > max ? s.Substring(0, max) : s;
    }

    // need22-B02：子代理发包路径说明（契约见 docs/need22/backend/02-发包接入与副作用修正.md §2.3）：
    // 子代理的 prompt 由主代理构造（不含用户手输的 [[skill:...]] 标记），且子会话标题由
    // WorkspaceSessionService.AppendSessionMessageAsync 统一生成（标题 strip 已覆盖），
    // 因此本期不在子代理路径单独接技能展开；若将来子代理支持用户标记，
    // 其发包复用 AgentOrchestrator/TurnPlanner 时展开会自动生效。

    public IReadOnlyList<SubHandle> GetActive(string parentSessionId)
    {
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            lock (list)
            {
                return list.ToList();
            }
        }
        return Array.Empty<SubHandle>();
    }

    /// <summary>need13：级联取消（先子后主由 Bridge 层组装；此处只掐子 CTS）。</summary>
    public int CancelTree(string parentSessionId)
    {
        int n = 0;
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            List<SubHandle> snap;
            lock (list)
            {
                snap = list.ToList();
            }
            foreach (var h in snap)
            {
                try
                {
                    h.Cts.Cancel();
                    n++;
                }
                catch { }
            }
        }
        return n;
    }

    public bool CancelSub(string parentSessionId, string subSessionId)
    {
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            SubHandle? h;
            lock (list)
            {
                h = list.FirstOrDefault(x =>
                    string.Equals(x.SubSessionId, subSessionId, StringComparison.OrdinalIgnoreCase)
                );
            }
            if (h != null)
            {
                try
                {
                    h.Cts.Cancel();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>need13：启动自愈（running 残留置 cancelled + 落关闭 notice）。</summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        int n = 0;
        try
        {
            var workspaces = await _sessions.GetAllWorkspacesAsync(ct).ConfigureAwait(false);
            foreach (var ws in workspaces)
            {
                List<SessionMetadata> all;
                try
                {
                    // 读全量 index（含子）：经 GetSubSessions 逐主扫太贵，直接走仓储由 Service 侧补接口前先用 GetSessionsByWorkspaceAsync + 兜底
                    all = await _sessions
                        .GetSessionsByWorkspaceAsync(ws.Id, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                foreach (var m in all)
                {
                    if (!string.Equals(m.AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (
                        !string.Equals(m.SubStatus, "running", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            m.SubStatus,
                            "pending",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    try
                    {
                        m.SubStatus = "cancelled";
                        m.UpdatedAtUtc = DateTime.UtcNow;
                        await _sessions
                            .AppendSessionMessageAsync(
                                ws.Id,
                                m.Id,
                                UnifiedChatMessage.Assistant(
                                    "[系统恢复] 该子代理因宿主重启已终止，未完成部分请由主重新 spawn。"
                                ),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        n++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return n;
    }

    private async Task SetSubStatusAsync(string workspaceId, string subSessionId, string status)
    {
        try
        {
            var meta = await _sessions
                .GetSessionAsync(workspaceId, subSessionId)
                .ConfigureAwait(false);
            if (meta == null)
                return;
            meta.SubStatus = status;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await _sessions
                .AppendSessionMessageAsync(
                    workspaceId,
                    subSessionId,
                    UnifiedChatMessage.Assistant($"[SubStatus] {status}"),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            // Append 已刷 index（含 SubStatus）；无需额外写 meta
        }
        catch { }
    }

    private async Task<string> ReadLastAssistantTextAsync(string workspaceId, string subSessionId)
    {
        try
        {
            var msgs = await _sessions
                .GetSessionMessagesAsync(workspaceId, subSessionId)
                .ConfigureAwait(false);
            for (int i = msgs.Count - 1; i >= 0; i--)
            {
                var m = msgs[i];
                if (!string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool hasCalls = m.ToolCalls != null && m.ToolCalls.Count > 0;
                if (hasCalls)
                    continue;
                if (!string.IsNullOrWhiteSpace(m.Content))
                    return m.Content!;
            }
            var last = msgs.LastOrDefault();
            return (last?.Content ?? string.Empty) + "\n[子未输出总结]";
        }
        catch
        {
            return "[子未输出总结]";
        }
    }

    /// <summary>need22-P1 T7：累加子会话全部消息用量（多轮子耗全量；替代只读末条 usage 的旧口径）。</summary>
    private async Task<(long In, long Out, long Cached)> ReadTotalUsageAsync(
        string workspaceId,
        string subSessionId
    )
    {
        try
        {
            var msgs = await _sessions
                .GetSessionMessagesAsync(workspaceId, subSessionId)
                .ConfigureAwait(false);
            long inp = 0,
                outp = 0,
                cached = 0;
            foreach (var m in msgs)
            {
                inp += m.InputTokens ?? 0;
                outp += m.OutputTokens ?? 0;
                cached += m.CachedTokens ?? 0;
            }
            return (inp, outp, cached);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static void Publish(string sessionId, string kind, object payload)
    {
        try
        {
            HaloAgent.Bridge.Contracts.BridgeEvents.Publish(
                "agent:turn-event",
                new
                {
                    sessionId,
                    kind,
                    payload,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            );
        }
        catch { }
    }
}
