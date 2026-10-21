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

/// <summary>need21-B03: 调度器（超时包装 + 流水线装配：Setup→Request→Call→Persist→Decide→Exec）。</summary>
public sealed partial class AgentOrchestrator
{
    private int ResolveTurnTimeoutMs()
    {
        try
        {
            if (_agentConfigService != null)
            {
                int v = _agentConfigService.GetInternal().TurnTimeoutMs;
                if (v <= 0)
                    return AgentDefaults.TurnTimeoutMs;
                return Math.Clamp(
                    v,
                    AgentDefaults.TurnTimeoutMinMs,
                    AgentDefaults.HardTimeoutCeilMs
                );
            }
        }
        catch
        { /* 配置读取失败回默认不断会话 */
        }
        return AgentDefaults.TurnTimeoutMs;
    }

    private static string TurnTimeoutMessage(int ms) =>
        $"本轮执行超过总体超时（{ms / 1000}s）已终止，已落盘片段予以保留；长任务请拆小步后继续，或调大 config/agent.json 的 turnTimeoutMs（默认 600000 即 10min）。";

    private async Task<AgentTurnResult> RunAgentTurnCoreAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default
    )
    {
        var prep = await PrepareTurnAsync(
                workspaceId,
                sessionId,
                userText,
                providerId,
                modelId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (prep.Terminal != null)
            return prep.Terminal;
        var ctx = prep.Ctx!;
        // (2) ReAct 循环（need08：ctx.EffectiveMax<=0 即无限轮次，永不进截断分支）
        for (int iteration = 1; ctx.EffectiveMax <= 0 || iteration <= ctx.EffectiveMax; iteration++)
        {
            var (headOutcome, batch) = await BuildRequestAsync(
                    ctx,
                    iteration,
                    workspaceId,
                    sessionId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (headOutcome.Terminal != null)
                return headOutcome.Terminal;
            var request = batch!.Request;

            var (callOutcome, streamState) = await CallAsync(
                    ctx,
                    iteration,
                    workspaceId,
                    sessionId,
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (callOutcome.Terminal != null)
                return callOutcome.Terminal;
            var pendingTools = streamState!.PendingTools;
            var toolOrder = streamState.ToolOrder;
            // 汇总 AssistantMessage 并持久化
            var persistOutcome = await PersistAssistantAsync(
                    ctx,
                    iteration,
                    workspaceId,
                    sessionId,
                    pendingTools,
                    toolOrder,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (persistOutcome.Terminal != null)
                return persistOutcome.Terminal;
            var (decideOutcome, proceedToDispatch) = await DecideAsync(
                    ctx,
                    iteration,
                    workspaceId,
                    sessionId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (decideOutcome.Terminal != null)
                return decideOutcome.Terminal;
            if (!proceedToDispatch)
                continue;
            var execOutcome = await ExecToolsAsync(
                    ctx,
                    iteration,
                    workspaceId,
                    sessionId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (execOutcome.Terminal != null)
                return execOutcome.Terminal;
        }

        // 理论不可达（有限循环内 iteration>=ctx.EffectiveMax 即返回；无限循环内必 return），兜底截断
        PublishTurnEvent(
            sessionId,
            "TurnTruncated",
            new
            {
                iterations = ctx.EffectiveMax,
                ctx.ToolExecutions,
                maxIterations = ctx.EffectiveMax
            }
        );
        UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
        return AgentTurnResult.Truncated(ctx.EffectiveMax, ctx.ToolExecutions);
    }
}
