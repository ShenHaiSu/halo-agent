using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>need13：子代理句柄（内存注册表项）。</summary>
public sealed class SubHandle
{
    public string SubSessionId { get; init; } = string.Empty;
    public string CallId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Task<AgentTurnResult> Task { get; set; } =
        System.Threading.Tasks.Task.FromResult(AgentTurnResult.Cancelled(0, 0));
    public CancellationTokenSource Cts { get; init; } = new();
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>need13：子代理 spawn 请求（Orchestrator 分流解析产物）。</summary>
public sealed class SpawnCallSpec
{
    public string CallId { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ProviderId { get; init; }
    public int? MaxIterations { get; init; }
}

/// <summary>need13：子代理结果（落主历史 Tool(callId, json, "spawn_subagent")）。</summary>
public sealed class SubAgentResult
{
    public string CallId { get; init; } = string.Empty;
    public string SubSessionId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int ToolExecutions { get; init; }
    public int Iterations { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Warning { get; init; }

    /// <summary>need22-P1 T4：Barrier 超时标记（主轮继续，超时子按 summary 截断回填）。</summary>
    public bool TimedOut { get; init; }

    public string ToJson() =>
        JsonSerializer.Serialize(
            new
            {
                subSessionId = SubSessionId,
                label = Label,
                success = Success,
                summary = Summary,
                toolExecutions = ToolExecutions,
                iterations = Iterations,
                usage = new
                {
                    input = InputTokens,
                    output = OutputTokens,
                    cached = CachedTokens
                },
                errorCode = ErrorCode,
                errorMessage = ErrorMessage,
                warning = Warning,
                timedOut = TimedOut,
            },
            HaloJsonOptions.CompactCamelCase
        );
}

/// <summary>need23：子会话运行统计（轮次/工具数/最近工具名），供设置中心·子运行监控卡。</summary>
public sealed class SubRuntimeStats
{
    /// <summary>已完成轮次（assistant 轮数）。</summary>
    public int Iterations { get; init; }

    /// <summary>工具调用总次数。</summary>
    public int ToolCount { get; init; }

    /// <summary>最近一次工具名（无则空串）。</summary>
    public string LastTool { get; init; } = string.Empty;
}

/// <summary>need16：子用量快照复用（Bridge 详情与 Coordinator 共用）。</summary>
public static class SubStatsReader
{
    /// <summary>need23：读子会话消息（失败降级为空列表，调用方不再各自 try/catch）。</summary>
    private static async Task<List<UnifiedChatMessage>> LoadMessagesAsync(
        IWorkspaceSessionService sessions,
        string workspaceId,
        string subSessionId
    )
    {
        try
        {
            return await sessions
                .GetSessionMessagesAsync(workspaceId, subSessionId)
                .ConfigureAwait(false);
        }
        catch
        {
            return new List<UnifiedChatMessage>();
        }
    }

    /// <summary>need23：运行监控统计（轮次/工具数/最近工具名）。</summary>
    public static async Task<SubRuntimeStats> ReadRuntimeStatsAsync(
        IWorkspaceSessionService sessions,
        string workspaceId,
        string subSessionId
    )
    {
        var msgs = await LoadMessagesAsync(sessions, workspaceId, subSessionId)
            .ConfigureAwait(false);
        int tools = 0;
        int iters = 0;
        string lastTool = string.Empty;
        foreach (var m in msgs)
        {
            if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                tools++;
                if (!string.IsNullOrWhiteSpace(m.Name))
                    lastTool = m.Name!;
            }
            if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (m.ToolCalls != null)
                    iters++;
                else if (!string.IsNullOrWhiteSpace(m.Content))
                    iters++;
            }
        }
        iters = Math.Max(
            iters,
            msgs.Count(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        );
        return new SubRuntimeStats
        {
            Iterations = iters,
            ToolCount = tools,
            LastTool = lastTool,
        };
    }

    public static async Task<(
        long In,
        long Out,
        long Cached,
        int Msgs,
        int Tools,
        int Iters
    )> ReadStatsAsync(IWorkspaceSessionService sessions, string workspaceId, string subSessionId)
    {
        try
        {
            var msgs = await LoadMessagesAsync(sessions, workspaceId, subSessionId)
                .ConfigureAwait(false);
            var last = msgs.LastOrDefault(m => m.InputTokens.HasValue && m.InputTokens.Value > 0);
            long inp = last?.InputTokens ?? 0;
            long outp = 0;
            long cached = last?.CachedTokens ?? 0;
            int tools = 0;
            int iters = 0;
            foreach (var m in msgs)
            {
                if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
                    tools++;
                if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    outp += m.OutputTokens ?? 0;
                    if (m.ToolCalls != null)
                        iters++;
                    else if (!string.IsNullOrWhiteSpace(m.Content))
                        iters++;
                }
            }
            return (
                inp,
                outp,
                cached,
                msgs.Count,
                tools,
                Math.Max(
                    iters,
                    msgs.Count(m =>
                        string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    )
                )
            );
        }
        catch
        {
            return (0, 0, 0, 0, 0, 0);
        }
    }
}
