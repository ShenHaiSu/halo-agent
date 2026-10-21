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

/// <summary>need21-B03: LLM 调用辅助（落盘/截断/采样）。</summary>
public sealed partial class AgentOrchestrator
{
    internal static string SpillLargeToolOutput(
        string toolText,
        string? dataRoot,
        string workspaceId,
        string sessionId,
        string callId
    )
    {
        try
        {
            if (string.IsNullOrEmpty(toolText))
                return toolText;
            long approxBytes;
            try
            {
                approxBytes = System.Text.Encoding.UTF8.GetByteCount(toolText);
            }
            catch
            {
                approxBytes = toolText.Length;
            }
            if (approxBytes <= ToolOutputSpillThresholdBytes)
                return toolText;
            string Safe(string s, int max)
            {
                if (string.IsNullOrWhiteSpace(s))
                    return "x";
                var chars = s.Trim()
                    .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
                    .ToArray();
                string r = new string(chars);
                return r.Length > max ? r.Substring(0, max) : r;
            }
            string baseDir = string.IsNullOrWhiteSpace(dataRoot)
                ? System.IO.Path.GetTempPath()
                : dataRoot;
            string dir = System.IO.Path.Combine(
                baseDir,
                "tool-outputs",
                Safe(workspaceId, 32) + "_" + Safe(sessionId, 32)
            );
            System.IO.Directory.CreateDirectory(dir);
            string fileName =
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Safe(callId, 32) + ".txt";
            string abs = System.IO.Path.Combine(dir, fileName);
            System.IO.File.WriteAllText(abs, toolText, System.Text.Encoding.UTF8);
            long size = 0;
            try
            {
                size = new System.IO.FileInfo(abs).Length;
            }
            catch
            {
                size = approxBytes;
            }
            string preview =
                toolText.Length > ToolOutputPreviewChars
                    ? toolText.Substring(0, ToolOutputPreviewChars)
                    : toolText;
            return preview
                + $"\n...（工具输出约 {approxBytes} 字节，超 64KB 已落盘，仅保留前 4K 预览；全文见 fileRef）\n[fileRef path=\"{abs}\" size={size}]";
        }
        catch
        {
            return toolText.Length > 8000
                ? toolText.Substring(0, 8000) + "\n...（输出过长已截断）"
                : toolText;
        }
    }

    private static string TruncateForEvent(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Length > max ? s.Substring(0, max) + "\n...（输出过长已截断）" : s;
    }

    private void PublishTurnEvent(string sessionId, string kind, object payload)
    {
        try
        {
            _sink.EmitAsync(sessionId, kind, payload).GetAwaiter().GetResult();
        }
        catch { }
    }

    private void PublishStreamDelta(string sessionId, string kind, string textDelta)
    {
        try
        {
            _sink.EmitStreamDelta(sessionId, kind, textDelta);
        }
        catch { }
    }

    public sealed class PendingToolCall
    {
        public string CallId { get; init; } = string.Empty;
        public string? ItemId { get; set; }
        public string FunctionName { get; set; } = string.Empty;
        public System.Text.StringBuilder ArgsBuilder { get; } = new();
        public long ReceivedBytes = 0;
        public long LastPublishedBytes = -1;
        public long LastPublishTick = 0;
    }

    private async Task<(PhaseOutcome Outcome, LlmStreamState? State)> CallAsync(
        TurnContext ctx,
        int iteration,
        string workspaceId,
        string sessionId,
        UnifiedLlmRequest request,
        CancellationToken cancellationToken
    )
    {
        // (5) 流式推理：汇总增量并实时推送
        var textAcc = new System.Text.StringBuilder();
        var reasoningAcc = new System.Text.StringBuilder();
        var pendingTools = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        var toolOrder = new List<string>();
        UnifiedTokenUsage? usage = null;
        bool streamCancelled = false;
        bool streamTruncated = false; // NEED17-06 §6.4
        string? streamErrorCode = null;
        string? streamErrorMessage = null;
        long ttftMark = Environment.TickCount64;
        bool ttftSent = false;

        PublishTurnEvent(
            sessionId,
            "IterationStarted",
            new
            {
                iteration,
                maxIterations = ctx.EffectiveMax,
                agentRole = AgentRole,
                subEnabled = SubPolicy.SubAgentEnabled,
                subMaxParallel = SubPolicy.SubMaxParallel,
                subMaxIterations = SubPolicy.SubMaxIterations
            }
        );

        try
        {
            await foreach (
                var evt in _gatewayClient
                    .StreamAsync(request, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                switch (evt.Kind)
                {
                    case UnifiedStreamEventKind.ReasoningDelta:
                        if (!string.IsNullOrEmpty(evt.TextDelta))
                        {
                            reasoningAcc.Append(evt.TextDelta);
                            PublishStreamDelta(sessionId, "ReasoningDelta", evt.TextDelta);
                        }
                        break;
                    case UnifiedStreamEventKind.TextDelta:
                        if (!string.IsNullOrEmpty(evt.TextDelta))
                        {
                            if (!ttftSent)
                            {
                                ttftSent = true;
                                PublishTurnEvent(
                                    sessionId,
                                    "FirstToken",
                                    new { iteration, ttftMs = Environment.TickCount64 - ttftMark }
                                );
                            }
                            textAcc.Append(evt.TextDelta);
                            PublishStreamDelta(sessionId, "TextDelta", evt.TextDelta);
                        }
                        break;
                    case UnifiedStreamEventKind.ToolCallStarted:
                        if (evt.ToolCall != null && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId))
                        {
                            string cid = evt.ToolCall.CallId;
                            if (!pendingTools.TryGetValue(cid, out var pending))
                            {
                                pending = new PendingToolCall
                                {
                                    CallId = cid,
                                    ItemId = evt.ToolCall.ItemId,
                                    FunctionName = evt.ToolCall.FunctionName ?? string.Empty
                                };
                                pendingTools[cid] = pending;
                                toolOrder.Add(cid);
                            }
                            else if (!string.IsNullOrWhiteSpace(evt.ToolCall.FunctionName))
                            {
                                pending.FunctionName = evt.ToolCall.FunctionName;
                            }
                            PublishTurnEvent(
                                sessionId,
                                "ToolCallStarted",
                                new
                                {
                                    iteration,
                                    callId = cid,
                                    functionName = pending.FunctionName,
                                    receivedBytes = 0L
                                }
                            );
                        }
                        break;
                    case UnifiedStreamEventKind.ToolCallArgsDelta:
                        if (
                            evt.ToolCall != null
                            && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId)
                            && evt.TextDelta != null
                        )
                        {
                            if (pendingTools.TryGetValue(evt.ToolCall.CallId, out var p))
                            {
                                p.ArgsBuilder.Append(evt.TextDelta);
                                try
                                {
                                    p.ReceivedBytes += System.Text.Encoding.UTF8.GetByteCount(
                                        evt.TextDelta
                                    );
                                }
                                catch
                                {
                                    p.ReceivedBytes = p.ArgsBuilder.Length;
                                }
                                bool isFileWrite =
                                    p.FunctionName is "write_file" or "edit_file" or "edit_lines";
                                long now = Environment.TickCount64;
                                bool throttleHit =
                                    (now - p.LastPublishTick) >= 150 || evt.TextDelta.Length > 4096;
                                if (isFileWrite ? throttleHit : true)
                                {
                                    p.LastPublishTick = now;
                                    p.LastPublishedBytes = p.ReceivedBytes;
                                    PublishTurnEvent(
                                        sessionId,
                                        "ToolCallUpdated",
                                        new
                                        {
                                            iteration,
                                            callId = evt.ToolCall.CallId,
                                            functionName = p.FunctionName,
                                            argsDelta = evt.TextDelta,
                                            receivedBytes = p.ReceivedBytes,
                                            argsLength = p.ArgsBuilder.Length
                                        }
                                    );
                                }
                            }
                        }
                        break;
                    case UnifiedStreamEventKind.ToolCallCompleted:
                        if (evt.ToolCall != null && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId))
                        {
                            string cid2 = evt.ToolCall.CallId;
                            if (!pendingTools.TryGetValue(cid2, out var done2))
                            {
                                done2 = new PendingToolCall
                                {
                                    CallId = cid2,
                                    ItemId = evt.ToolCall.ItemId
                                };
                                pendingTools[cid2] = done2;
                                toolOrder.Add(cid2);
                            }
                            if (!string.IsNullOrWhiteSpace(evt.ToolCall.FunctionName))
                            {
                                done2.FunctionName = evt.ToolCall.FunctionName;
                            }
                            if (!string.IsNullOrWhiteSpace(evt.ToolCall.ArgumentsJson))
                            {
                                done2.ArgsBuilder.Clear();
                                done2.ArgsBuilder.Append(evt.ToolCall.ArgumentsJson);
                                try
                                {
                                    done2.ReceivedBytes = System.Text.Encoding.UTF8.GetByteCount(
                                        evt.ToolCall.ArgumentsJson
                                    );
                                }
                                catch { }
                            }
                            // P1：把完整参数实时推送给前端，转入对应会话桶，避免卡片参数区永久为空
                            PublishTurnEvent(
                                sessionId,
                                "ToolCallUpdated",
                                new
                                {
                                    iteration,
                                    callId = cid2,
                                    functionName = done2.FunctionName,
                                    argsJson = done2.ArgsBuilder.ToString(),
                                    receivedBytes = done2.ReceivedBytes,
                                    argsLength = done2.ArgsBuilder.Length
                                }
                            );
                        }
                        break;
                    case UnifiedStreamEventKind.UsageUpdated:
                        usage = evt.Usage;
                        // need05-01：账单累计（本轮内多次 LLM 调用的 Σ；streamError/cancel 分支直接返回，此处不回滚但不发布）
                        if (evt.Usage != null)
                        {
                            ctx.TurnBilledIn += evt.Usage.InputTokens;
                            ctx.TurnBilledOut += evt.Usage.OutputTokens;
                        }
                        break;
                    case UnifiedStreamEventKind.StreamCancelled:
                        streamCancelled = true;
                        break;
                    case UnifiedStreamEventKind.StreamTruncated: // NEED17-06 §6.4
                        streamTruncated = true;
                        break;
                    case UnifiedStreamEventKind.StreamError:
                        streamErrorCode = evt.ErrorCode ?? "STREAM_ERROR";
                        streamErrorMessage = evt.ErrorMessage ?? "流式推理失败";
                        break;
                    case UnifiedStreamEventKind.StreamCompleted:
                        break;
                }

                if (streamCancelled || streamErrorCode != null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
            // need12-P0/P2：流内抛取消同样先落袋本轮（有实测用实测，无实测用估算），再保住前面 iteration。
            UsageRecorder.CollectAuto(
                ctx.UsageRows,
                ctx.Provider,
                ctx.Model,
                usage,
                UsageRecorder.EstimateInput(ctx.RequestMessages, ctx.SystemPrompt),
                UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                "cancelled",
                ctx.RequestId,
                sessionId,
                workspaceId,
                UsageRecorder.SourceAgent
            );
            UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(AgentTurnResult.Cancelled(iteration - 1, ctx.ToolExecutions)),
                null
            );
        }

        if (cancellationToken.IsCancellationRequested || streamCancelled)
        {
            // 持久化已有部分输出，避免前端空白
            if (textAcc.Length > 0 || reasoningAcc.Length > 0)
            {
                try
                {
                    var partial = UnifiedChatMessage.Assistant(
                        textAcc.Length > 0 ? textAcc.ToString() : null,
                        null,
                        reasoningAcc.Length > 0 ? reasoningAcc.ToString() : null
                    );
                    await _sessionService
                        .AppendSessionMessageAsync(
                            workspaceId,
                            sessionId,
                            partial,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                catch
                { /* 取消路径下持久化失败不掩盖取消语义 */
                }
            }
            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
            // need12-P2：有实测用实测，无实测用估算（input 全量 + 已产出文本），保证取消不低估。
            UsageRecorder.CollectAuto(
                ctx.UsageRows,
                ctx.Provider,
                ctx.Model,
                usage,
                UsageRecorder.EstimateInput(ctx.RequestMessages, ctx.SystemPrompt),
                UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                "cancelled",
                ctx.RequestId,
                sessionId,
                workspaceId,
                UsageRecorder.SourceAgent
            );
            UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(AgentTurnResult.Cancelled(iteration - 1, ctx.ToolExecutions)),
                null
            );
        }
        if (streamErrorCode != null)
        {
            try
            {
                var errMsg = UnifiedChatMessage.Assistant(
                    $"本次推理遇到错误（{streamErrorCode}）：{streamErrorMessage}"
                );
                await _sessionService
                    .AppendSessionMessageAsync(
                        workspaceId,
                        sessionId,
                        errMsg,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch
            { /* ignore */
            }
            PublishTurnEvent(
                sessionId,
                "TurnError",
                new
                {
                    iteration,
                    errorCode = streamErrorCode,
                    errorMessage = streamErrorMessage
                }
            );
            // need12-P2：错误同样智能落袋（有实测用实测，无实测用估算）。
            UsageRecorder.CollectAuto(
                ctx.UsageRows,
                ctx.Provider,
                ctx.Model,
                usage,
                UsageRecorder.EstimateInput(ctx.RequestMessages, ctx.SystemPrompt),
                UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                "error",
                ctx.RequestId,
                sessionId,
                workspaceId,
                UsageRecorder.SourceAgent
            );
            UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(
                    AgentTurnResult.Failed(
                        streamErrorCode,
                        streamErrorMessage ?? string.Empty,
                        iteration,
                        ctx.ToolExecutions
                    )
                ),
                null
            );
        }

        ctx.LlmText = textAcc.ToString();
        ctx.LlmReasoning = reasoningAcc.Length > 0 ? reasoningAcc.ToString() : null;
        ctx.CurrentUsage = usage;
        ctx.StreamTruncated = streamTruncated;
        var state = new LlmStreamState
        {
            Request = request,
            Usage = usage,
            StreamTruncated = streamTruncated
        };
        state.PendingTools = pendingTools;
        state.ToolOrder = toolOrder;
        return (PhaseOutcome.Continue(), state);
    }

    /// <summary>need22-P1 T6：大 toolOutput（>64KB 按 UTF-8 字节）先落盘再引用 fileRef{path,size}，content 只留前 4K 预览。
    /// 落盘失败回退为 8K 截断；messages.jsonl 信封格式不变（仍是 tool 文本消息）。</summary>
    internal const long ToolOutputSpillThresholdBytes = 64L * 1024;
    internal const int ToolOutputPreviewChars = 4096;
}
