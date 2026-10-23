using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Services.Agent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>need21-B03: 历史落盘/用量刷新。</summary>
public sealed partial class AgentOrchestrator
{
    private void CollectUsageRow(
        List<PendingUsageRow> rows,
        ProviderConfig provider,
        ModelConfig model,
        UnifiedTokenUsage? usage,
        string status,
        string requestId,
        string sessionId,
        string workspaceId
    )
    {
        // need12-compat：老入口保留，统一转调 UsageRecorder（source=agent）。
        UsageRecorder.Collect(
            rows,
            provider,
            model,
            usage,
            status,
            requestId,
            sessionId,
            workspaceId,
            UsageRecorder.SourceAgent
        );
    }

    private void FlushUsageRows(List<PendingUsageRow> rows, string sessionId, string workspaceId)
    {
        UsageRecorder.Flush(_usageMetrics, rows, sessionId, workspaceId);
    }

    /// <summary>need13：spawn 回包落盘助手（Tool(callId, text, "spawn_subagent")）。</summary>
    private async Task AppendToolTextAsync(
        string workspaceId,
        string sessionId,
        string callId,
        string toolName,
        string text,
        CancellationToken ct
    )
    {
        var toolMessage = UnifiedChatMessage.Tool(callId, text, toolName);
        try
        {
            await _sessionService
                .AppendSessionMessageAsync(workspaceId, sessionId, toolMessage, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { }
        PublishTurnEvent(
            sessionId,
            "ToolExecutionCompleted",
            new
            {
                iteration = 0,
                callId,
                functionName = toolName,
                success = !text.StartsWith("[SUBAGENT_DISABLED]", StringComparison.Ordinal)
                    && !text.StartsWith("[SUBAGENT_QUOTA_EXCEEDED]", StringComparison.Ordinal)
                    && !text.StartsWith("[SUBAGENT_VALIDATION]", StringComparison.Ordinal)
                    && !text.StartsWith("[SUBAGENT_NESTED_DENIED]", StringComparison.Ordinal),
                exitCode = 0,
                durationMs = 0L,
                argsJson = string.Empty,
                resultText = TruncateForEvent(text, 8000),
                fileBytes = string.Empty,
                updatedAtUtc = DateTime.UtcNow.ToString("o")
            }
        );
    }

    private static string BuildMissingFieldDraft(ToolResult toolResult, string? argumentsJson)
    {
        var fields = new List<string>();
        try
        {
            string payload = toolResult?.ErrorPayloadJson ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ExtractJsonObject(payload));
                var root = doc.RootElement;
                if (
                    root.TryGetProperty("missing", out var m)
                    && m.ValueKind == System.Text.Json.JsonValueKind.Array
                )
                {
                    foreach (var e in m.EnumerateArray())
                        if (
                            e.ValueKind == System.Text.Json.JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(e.GetString())
                        )
                            fields.Add(e.GetString()!);
                }
                else if (
                    root.TryGetProperty("field", out var f)
                    && f.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(f.GetString())
                )
                {
                    fields.Add(f.GetString()!);
                }
            }
        }
        catch
        { /* 解析失败则走回退 */
        }
        if (fields.Count == 0)
        {
            try
            {
                string err = toolResult?.Error ?? string.Empty;
                int q1 = err.IndexOf('\'');
                int q2 = q1 >= 0 ? err.IndexOf('\'', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1 + 1)
                {
                    string cand = err.Substring(q1 + 1, q2 - q1 - 1).Trim();
                    if (cand.Length > 0 && cand.Length <= 64)
                        fields.Add(cand);
                }
            }
            catch
            { /* ignore */
            }
        }
        if (fields.Count == 0)
            return "{\"missing-field\":\"<fill>\"}";
        string draft =
            "{"
            + string.Join(
                ",",
                fields
                    .Take(5)
                    .Select(n =>
                        "\"" + n.Replace("\\", "/").Replace("\"", "\\\"") + "\":\"<fill>\""
                    )
            )
            + "}";
        return draft.Length > 300 ? draft.Substring(0, 300) : draft;
    }

    private async Task<PhaseOutcome> PersistAssistantAsync(
        TurnContext ctx,
        int iteration,
        string workspaceId,
        string sessionId,
        Dictionary<string, PendingToolCall> pendingTools,
        List<string> toolOrder,
        CancellationToken cancellationToken
    )
    {
        var completedCalls = new List<UnifiedToolCall>();
        var emptyNameCids = new List<string>();
        foreach (string cid in toolOrder)
        {
            var p = pendingTools[cid];
            if (string.IsNullOrWhiteSpace(p.FunctionName))
            {
                emptyNameCids.Add(cid);
                continue;
            }
            completedCalls.Add(
                new UnifiedToolCall(cid, p.ItemId, p.FunctionName, p.ArgsBuilder.ToString())
            );
        }
        // need22-P1 T3：空 FunctionName 收集（回包落在 assistant 之后，保证 tool_calls→tool 先后序；见 AssistantMessagePersisted 后处理）。
        var assistantMessage = UnifiedChatMessage.Assistant(
            !string.IsNullOrEmpty(ctx.LlmText) ? ctx.LlmText : null,
            completedCalls.Count > 0 ? completedCalls : null,
            !string.IsNullOrEmpty(ctx.LlmReasoning) ? ctx.LlmReasoning : null
        );
        if (ctx.CurrentUsage != null)
        {
            assistantMessage.InputTokens = ctx.CurrentUsage.InputTokens;
            assistantMessage.OutputTokens = ctx.CurrentUsage.OutputTokens;
            assistantMessage.CachedTokens = ctx.CurrentUsage.CachedInputTokens;
        }
        // need12：本 iteration 的 HTTP 请求落袋（纯内存，轮末统一写库；失败/取消分支在各自 return 前同样落袋）。
        // need12-P2：无 usage 实测时退化为 degraded 估算行（input 全量估算，output 用已产出文本估算）。
        UsageRecorder.CollectAuto(
            ctx.UsageRows,
            ctx.Provider,
            ctx.Model,
            ctx.CurrentUsage,
            UsageRecorder.EstimateInput(ctx.RequestMessages, ctx.SystemPrompt),
            UsageRecorder.EstimateOutput(ctx.LlmText, ctx.LlmReasoning),
            ctx.CurrentUsage != null ? "ok" : "degraded",
            ctx.RequestId,
            sessionId,
            workspaceId,
            UsageRecorder.SourceAgent
        );

        try
        {
            await _sessionService
                .AppendSessionMessageAsync(
                    workspaceId,
                    sessionId,
                    assistantMessage,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
            // need12-P0：assistant 落盘时被取消，本 iteration 已落袋，保住前面行。
            UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
            return PhaseOutcome.End(AgentTurnResult.Cancelled(iteration, ctx.ToolExecutions));
        }
        PublishTurnEvent(
            sessionId,
            "AssistantMessagePersisted",
            new
            {
                iteration,
                hasToolCalls = completedCalls.Count > 0,
                toolCallCount = completedCalls.Count,
                inputTokens = ctx.CurrentUsage?.InputTokens ?? 0,
                outputTokens = ctx.CurrentUsage?.OutputTokens ?? 0,
                cachedTokens = ctx.CurrentUsage?.CachedInputTokens ?? 0,
                billedPrompt = ctx.TurnBilledIn,
                billedCompletion = ctx.TurnBilledOut,
                updatedAtUtc = DateTime.UtcNow.ToString("o")
            }
        );
        // need22-P1 T3：空 FunctionName 回包（assistant 之后，保证 tool_calls→tool 先后序）+ EMPTY_TOOL_NAME 连续失败终轮。
        if (emptyNameCids.Count > 0)
        {
            foreach (string ecid in emptyNameCids)
            {
                string emptyText = "[EMPTY_TOOL_NAME] 模型返回了缺少工具名的调用，已忽略。请用完整 functionName 重发该调用。";
                try
                {
                    await _sessionService
                        .AppendSessionMessageAsync(
                            workspaceId,
                            sessionId,
                            UnifiedChatMessage.Tool(ecid, emptyText, "unknown"),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
                    return PhaseOutcome.End(
                        AgentTurnResult.Cancelled(iteration, ctx.ToolExecutions)
                    );
                }
                catch
                { /* 落盘失败不掩盖错误码语义 */
                }
                PublishTurnEvent(
                    sessionId,
                    "ToolExecutionCompleted",
                    new
                    {
                        iteration,
                        callId = ecid,
                        functionName = "",
                        success = false,
                        exitCode = -1,
                        durationMs = 0L,
                        argsJson = "",
                        resultText = emptyText,
                        updatedAtUtc = DateTime.UtcNow.ToString("o")
                    }
                );
                ctx.ToolExecutions++;
            }
            // need22-P1 T3/无限模式兼容：无限轮次（ctx.EffectiveMax<=0）下取消即停（S08 契约），不提前截断；
            // 有限轮次下同码连续 3 次提前截断暂停（Success=true，可“继续”；与 maxIterations 截断同契约）。
            if (ctx.EffectiveMax > 0 && ctx.NoteToolError("EMPTY_TOOL_NAME"))
            {
                // need22-P1 T3：同码连续 3 次提前截断暂停（Success=true，可“继续”；与 maxIterations 截断同契约）。
                string msgEmpty3 =
                    "同一工具错误（EMPTY_TOOL_NAME：空工具名）连续出现 3 次，已暂停自动执行。请检查模型输出后发送“继续”以恢复任务。";
                try
                {
                    await _sessionService
                        .AppendSessionMessageAsync(
                            workspaceId,
                            sessionId,
                            UnifiedChatMessage.Assistant(msgEmpty3),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                catch
                { /* ignore */
                }
                PublishTurnEvent(
                    sessionId,
                    "TurnTruncated",
                    new
                    {
                        iterations = iteration,
                        ctx.ToolExecutions,
                        maxIterations = ctx.EffectiveMax,
                        reason = "repeated-tool-error",
                        errorCode = "EMPTY_TOOL_NAME"
                    }
                );
                UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
                return PhaseOutcome.End(AgentTurnResult.Truncated(iteration, ctx.ToolExecutions));
            }
        }

        ctx.CompletedCalls = completedCalls;
        ctx.EmptyNameCids = emptyNameCids;
        return PhaseOutcome.Continue();
    }

    private static string ExtractJsonObject(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "{}";
        }

        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return s.Substring(start, end - start + 1);
        }

        return "{}";
    }

    /// <summary>need22-P1 T6：大 toolOutput（>64KB 按 UTF-8 字节）先落盘再引用 fileRef{path,size}，content 只留前 4K 预览。
}
