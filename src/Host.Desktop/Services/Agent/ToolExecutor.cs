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

/// <summary>need21-B03: 工具执行阶段（原 RunAgentTurnCore 调度块整体搬迁）。</summary>
public sealed partial class AgentOrchestrator
{
    private async Task<PhaseOutcome> ExecToolsAsync(
        TurnContext ctx,
        int iteration,
        string workspaceId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        // (7) 依次调度执行每个 ToolCall（自愈：绝不抛异常中断）
        // need13：spawn 分流（normal 先串行，spawn 走 Coordinator Barrier；等待不消费主 iteration）
        var spawnCalls = ctx
            .CompletedCalls.Where(c =>
                string.Equals(c.FunctionName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var normalCalls = ctx
            .CompletedCalls.Where(c =>
                !string.Equals(c.FunctionName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        foreach (var call in normalCalls)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0：工具执行前被取消，本 iteration 已落袋，保住前面行。
                UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
                return PhaseOutcome.End(AgentTurnResult.Cancelled(iteration, ctx.ToolExecutions));
            }

            PublishTurnEvent(
                sessionId,
                "ToolExecutionStarted",
                new
                {
                    iteration,
                    callId = call.CallId,
                    functionName = call.FunctionName,
                    argsJson = call.ArgumentsJson
                }
            );

            ToolResult toolResult;
            try
            {
                var dto = new ToolCallDto(call.CallId, call.FunctionName, call.ArgumentsJson);
                var toolContext = new ToolContext
                {
                    WorkspaceRoot = ctx.Workspace!.RootPath ?? string.Empty,
                    WorkspaceId = workspaceId,
                    SessionId = sessionId,
                    DataRoot = ctx.DataRoot,
                    CancellationToken = cancellationToken,
                    ExecDefaultTimeoutMsOverride = ctx.CfgExecDefaultMs,
                    ExecMaxTimeoutMsOverride = ctx.CfgExecMaxMs,
                    AgentRole = AgentRole,
                    AllowedCommandsOverride = ctx.CfgAllowedCommands,
                    ParentSessionId = string.Equals(
                        AgentRole,
                        "sub",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? (ctx.Session!.ParentSessionId ?? string.Empty)
                        : string.Empty,
                    SubAgentEnabled = SubPolicy.SubAgentEnabled,
                    SubMaxParallel = SubPolicy.SubMaxParallel,
                    SubMaxIterations = SubPolicy.SubMaxIterations,
                    SubAllowExecCommand = SubPolicy.SubAllowExecCommand,
                    SubAllowFileWrite = SubPolicy.SubAllowFileWrite,
                    SubAllowSkillWrite = SubPolicy.SubAllowSkillWrite,
                    SubAllowMemoryWrite = SubPolicy.SubAllowMemoryWrite,
                    SkillMemoryScopeDefault = string.Equals(
                        AgentRole,
                        "sub",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? "project"
                        : "project",
                };
                // need17-01/02/联调：执行走本轮冻结集 + strict 闭合开关（与声明同源；Off 回滚时未知字段降级 warning）。
                toolResult = await _toolRegistry
                    .DispatchAsync(
                        dto,
                        toolContext,
                        ctx.TurnActiveSet,
                        cancellationToken,
                        ctx.CfgClosedWorld
                    )
                    .ConfigureAwait(false);
                toolResult ??= ToolResult.Fail($"工具 '{call.FunctionName}' 返回空结果，请重试。");
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0：工具执行中被取消，本 iteration 已落袋，保住前面行。
                UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
                return PhaseOutcome.End(AgentTurnResult.Cancelled(iteration, ctx.ToolExecutions));
            }
            catch (Exception ex)
            {
                // 最后一道防线：任何意外异常都转自愈提示，绝不中断循环
                toolResult = ToolResult.Fail(
                    $"工具 '{call.FunctionName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。"
                );
            }

            ctx.ToolExecutions++;

            string errorCode =
                toolResult.ErrorCode ?? ToolResult.TryExtractCode(toolResult.Error) ?? string.Empty;
            string toolText = toolResult.Success
                ? (string.IsNullOrEmpty(toolResult.Output) ? "(工具执行成功，无输出)" : toolResult.Output)
                : $"[Tool Error - {call.FunctionName}]: {toolResult.Error}"
                    + (
                        string.IsNullOrEmpty(toolResult.Output)
                            ? string.Empty
                            : $"\nOutput: {toolResult.Output}"
                    )
                    + HintForTool(call.FunctionName, errorCode)
                    + BuildNextActionHint(
                        toolResult,
                        call.FunctionName,
                        call.ArgumentsJson,
                        errorCode
                    );
            // need22-P1 T6：大 toolOutput（>64KB）先落盘 fileRef{path,size}，content 只留前 4K 预览（messages.jsonl 信封格式不变）。
            toolText = SpillLargeToolOutput(
                toolText,
                ctx.DataRoot,
                workspaceId,
                sessionId,
                call.CallId
            );

            var toolMessage = UnifiedChatMessage.Tool(call.CallId, toolText, call.FunctionName);
            // need11-fix: view_image 等工具返回的 Images 必须挂到 tool 消息上，否则下一轮 PlanTurn 读不到，静默丢图（仅文本上游）。
            if (toolResult.Success && toolResult.Images != null && toolResult.Images.Count > 0)
            {
                toolMessage.Images = new List<Host.Desktop.Core.Models.ChatImageRef>(
                    toolResult.Images
                );
            }

            try
            {
                await _sessionService
                    .AppendSessionMessageAsync(
                        workspaceId,
                        sessionId,
                        toolMessage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0：tool 落盘时被取消，本 iteration 已落袋，保住前面行。
                UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
                return PhaseOutcome.End(AgentTurnResult.Cancelled(iteration, ctx.ToolExecutions));
            }
            // need23-R1：tool 消息落盘即告知前端（携带完整消息体，前端增量合并，
            // 不必等下一轮 AssistantMessagePersisted 的全量重拉；旧前端 default 分支忽略，零影响）。
            PublishTurnEvent(
                sessionId,
                "ToolMessagePersisted",
                new
                {
                    iteration,
                    messageId = toolMessage.Id,
                    callId = call.CallId,
                    role = "tool",
                    functionName = call.FunctionName,
                    toolCallId = toolMessage.ToolCallId,
                    content = TruncateForEvent(toolText, 8000),
                    images = toolMessage.Images,
                    createdAtUtc = toolMessage.CreatedAtUtc.ToString("o"),
                    updatedAtUtc = DateTime.UtcNow.ToString("o")
                }
            );
            PublishTurnEvent(
                sessionId,
                "ToolExecutionCompleted",
                new
                {
                    iteration,
                    callId = call.CallId,
                    functionName = call.FunctionName,
                    success = toolResult.Success,
                    exitCode = toolResult.ExitCode,
                    durationMs = toolResult.DurationMs,
                    argsJson = call.ArgumentsJson,
                    resultText = TruncateForEvent(toolText, 8000),
                    fileBytes = toolResult.FileBytes,
                    updatedAtUtc = DateTime.UtcNow.ToString("o")
                }
            );
            // need22-P1 T3：工具失败连续同码 3 次提前截断暂停（防空转多轮耗费；Success=true，可“继续”）。
            if (toolResult.Success)
                ctx.NoteToolSuccess();
            else
            {
                string failCode = string.IsNullOrEmpty(errorCode) ? "TOOL_FAILED" : errorCode;
                if (ctx.EffectiveMax > 0 && ctx.NoteToolError(failCode))
                {
                    string msgFail3 = $"同一工具错误（{failCode}）连续出现 3 次，已暂停自动执行。请检查参数后发送“继续”以恢复任务。";
                    try
                    {
                        await _sessionService
                            .AppendSessionMessageAsync(
                                workspaceId,
                                sessionId,
                                UnifiedChatMessage.Assistant(msgFail3),
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
                            iteration,
                            ctx.ToolExecutions,
                            maxIterations = ctx.EffectiveMax,
                            reason = "repeated-tool-error",
                            errorCode = failCode
                        }
                    );
                    FlushUsageRows(ctx.UsageRows, sessionId, workspaceId);
                    return PhaseOutcome.End(
                        AgentTurnResult.Truncated(iteration, ctx.ToolExecutions)
                    );
                }
            }
            // need13：spawn 分流（主才允许；子调 spawn 由 registry 禁用桩直接拒，不会走到这里——此处为纵深兜底）
        }
        if (spawnCalls.Count > 0)
        {
            if (!string.Equals(AgentRole, "main", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in spawnCalls)
                {
                    string fail =
                        "[SUBAGENT_NESTED_DENIED] 子代理内禁止再 spawn（深度=1）。本次已拒绝。请在当前子内串行完成，必要时记入总结交主代理拆分。";
                    await AppendToolTextAsync(
                            workspaceId,
                            sessionId,
                            s.CallId,
                            "spawn_subagent",
                            fail,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    ctx.ToolExecutions++;
                }
            }
            else if (!SubPolicy.SubAgentEnabled)
            {
                foreach (var s in spawnCalls)
                {
                    string fail =
                        "[SUBAGENT_DISABLED] 子代理已关闭（设置>智能体行为>子代理>总开关=false）。本次 spawn 已拒绝，未创建子会话。请不要重试 spawn，直接用主代理能力继续，并在回复中告知用户可在设置中重新开启。"
                        + HintForTool("spawn_subagent", "SUBAGENT_DISABLED");
                    await AppendToolTextAsync(
                            workspaceId,
                            sessionId,
                            s.CallId,
                            "spawn_subagent",
                            fail,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    ctx.ToolExecutions++;
                }
            }
            else
            {
                var specs = new List<SpawnCallSpec>();
                foreach (var s in spawnCalls)
                {
                    var (spec, vfail, _) = SubAgentCoordinator.ParseSpawnCall(
                        s.CallId,
                        s.ArgumentsJson
                    );
                    if (spec == null)
                    {
                        string fail =
                            (vfail ?? "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。")
                            + HintForTool("spawn_subagent", "SUBAGENT_VALIDATION");
                        // need23：落拒绝记录（设置中心·子运行监控卡可见）
                        _coordinator?.RecordRejection("SUBAGENT_VALIDATION", s.CallId, fail);
                        await AppendToolTextAsync(
                                workspaceId,
                                sessionId,
                                s.CallId,
                                "spawn_subagent",
                                fail,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        ctx.ToolExecutions++;
                    }
                    else
                        specs.Add(spec);
                }
                if (specs.Count > 0)
                {
                    var coord =
                        _coordinator
                        ?? throw new InvalidOperationException(
                            "need21-B02: SubAgentCoordinator unregistered"
                        );
                    var (handles, rejected) = await coord
                        .CreateSubsAsync(
                            workspaceId,
                            sessionId,
                            specs,
                            ctx.Session!.ProviderId ?? string.Empty,
                            ctx.Session!.ModelId ?? string.Empty,
                            SubPolicy,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    foreach (var r in rejected)
                    {
                        string fail =
                            r.FailText + HintForTool("spawn_subagent", "SUBAGENT_QUOTA_EXCEEDED");
                        await AppendToolTextAsync(
                                workspaceId,
                                sessionId,
                                r.CallId,
                                "spawn_subagent",
                                fail,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        ctx.ToolExecutions++;
                    }
                    PublishTurnEvent(
                        sessionId,
                        "SubAgentsBarrier",
                        new { total = handles.Count, waiting = true }
                    );
                    List<SubAgentResult> results;
                    try
                    {
                        results = await coord
                            .BarrierAsync(
                                workspaceId,
                                sessionId,
                                handles,
                                rejected,
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
                    foreach (var res in results)
                    {
                        await AppendToolTextAsync(
                                workspaceId,
                                sessionId,
                                res.CallId,
                                "spawn_subagent",
                                res.ToJson(),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        ctx.ToolExecutions++;
                    }
                    // need22-P1 T7：Barrier 全部子 usage 累加（本轮 TurnCompleted 回填真实子耗）。
                    ctx.SubTotalCount += results.Count;
                    foreach (var res in results)
                    {
                        ctx.SubBilledIn += res.InputTokens;
                        ctx.SubBilledOut += res.OutputTokens;
                    }
                    PublishTurnEvent(
                        sessionId,
                        "SubAgentsBarrierReleased",
                        new
                        {
                            subs = results
                                .Select(x => new
                                {
                                    callId = x.CallId,
                                    subId = x.SubSessionId,
                                    success = x.Success
                                })
                                .ToArray()
                        }
                    );
                }
            }
        }
        return PhaseOutcome.Continue();
    }
}
