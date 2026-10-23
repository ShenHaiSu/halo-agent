using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Bridge;

/// <summary>need21-B02: 会话跑轮。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<string>> StartSessionRun(string requestJson)
    {
        try
        {
            var req = JsonSerializer.Deserialize<UnifiedLlmRequest>(
                requestJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (req == null)
            {
                return Task.FromResult(
                    BridgeResponse<string>.Fail("INVALID_REQUEST", "请求 JSON 无法反序列化")
                );
            }

            string sessionId = !string.IsNullOrWhiteSpace(req.HeaderContext?.SessionId)
                ? req.HeaderContext.SessionId
                : req.RequestId;

            // 取消可能已经存在的旧会话生成任务
            if (_activeSessionTokens.TryRemove(sessionId, out var oldCts))
            {
                try
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }
                catch
                {
                    // 忽略旧任务清理异常
                }
            }

            var cts = new CancellationTokenSource();
            _activeSessionTokens[sessionId] = cts;

            // 在独立后台线程运行流式消费，不阻塞 Bridge 响应
            // need12-P1：直连会话同样落袋（source=session；无 workspaceId，WorkspaceId 置空）。
            var usageRows = new List<PendingUsageRow>();
            UnifiedTokenUsage? sessionUsage = null;
            string sessionStatus = "ok";
            string sessionRequestId = req.RequestId ?? Guid.NewGuid().ToString("N");
            var sessionTextAcc = new System.Text.StringBuilder();
            var sessionReasonAcc = new System.Text.StringBuilder();
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (
                            var evt in _llmGatewayClient
                                .StreamAsync(req, cts.Token)
                                .ConfigureAwait(false)
                        )
                        {
                            var dto = SessionStreamEventDto.FromUnifiedEvent(sessionId, evt);
                            JsEventDispatcher.Publish("llm:stream-event", dto);

                            if (
                                evt.Kind == UnifiedStreamEventKind.TextDelta
                                && !string.IsNullOrEmpty(evt.TextDelta)
                            )
                                sessionTextAcc.Append(evt.TextDelta);
                            else if (
                                evt.Kind == UnifiedStreamEventKind.ReasoningDelta
                                && !string.IsNullOrEmpty(evt.TextDelta)
                            )
                                sessionReasonAcc.Append(evt.TextDelta);
                            else if (
                                evt.Kind == UnifiedStreamEventKind.UsageUpdated
                                && evt.Usage != null
                            )
                                sessionUsage = evt.Usage;
                            else if (evt.Kind == UnifiedStreamEventKind.StreamCancelled)
                                sessionStatus = "cancelled";
                            else if (evt.Kind == UnifiedStreamEventKind.StreamError)
                                sessionStatus = "error";

                            if (
                                evt.Kind == UnifiedStreamEventKind.StreamCompleted
                                || evt.Kind == UnifiedStreamEventKind.StreamCancelled
                                || evt.Kind == UnifiedStreamEventKind.StreamError
                            )
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        sessionStatus = "cancelled";
                        var cancelDto = new SessionStreamEventDto
                        {
                            SessionId = sessionId,
                            Kind = nameof(UnifiedStreamEventKind.StreamCancelled)
                        };
                        JsEventDispatcher.Publish("llm:stream-event", cancelDto);
                    }
                    catch (Exception ex)
                    {
                        sessionStatus = "error";
                        HaloLogger.App.Error($"[HostBridge] Session {sessionId} 流式推送异常", ex);
                        var errDto = new SessionStreamEventDto
                        {
                            SessionId = sessionId,
                            Kind = nameof(UnifiedStreamEventKind.StreamError),
                            ErrorCode = "STREAM_EXECUTION_EXCEPTION",
                            ErrorMessage = ex.Message
                        };
                        JsEventDispatcher.Publish("llm:stream-event", errDto);
                    }
                    finally
                    {
                        try
                        {
                            // need12-P2：有实测用实测，无实测用估算（input 全量 + 已产出文本）。
                            UsageRecorder.CollectAuto(
                                usageRows,
                                req.Provider,
                                req.Model,
                                sessionUsage,
                                UsageRecorder.EstimateInput(req.Messages, req.SystemInstructions),
                                UsageRecorder.EstimateOutput(sessionTextAcc, sessionReasonAcc),
                                sessionStatus,
                                sessionRequestId,
                                sessionId,
                                string.Empty,
                                UsageRecorder.SourceSession
                            );
                            UsageRecorder.Flush(
                                UsageMetricsServiceInternal,
                                usageRows,
                                sessionId,
                                string.Empty
                            );
                        }
                        catch
                        { /* 用量落袋永不影响会话清理 */
                        }
                        _activeSessionTokens.TryRemove(sessionId, out _);
                        cts.Dispose();
                    }
                },
                CancellationToken.None
            );

            return Task.FromResult(BridgeResponse<string>.Success(sessionId, "流式请求已启动"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("StartSessionRun failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("SESSION_START_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 取消正在运行的会话流式请求：立即触发关联的 CancellationTokenSource.Cancel()，并在 100ms 内切断网络连接。
    /// </summary>
    public Task<BridgeResponse<bool>> CancelSessionRun(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    BridgeResponse<bool>.Fail("INVALID_SESSION_ID", "会话 ID 不能为空")
                );
            }

            if (_activeSessionTokens.TryRemove(sessionId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                HaloLogger.App.Info($"[HostBridge] 已成功请求取消 Session {sessionId} 的生成");
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已发送取消指令"));
            }

            return Task.FromResult(BridgeResponse<bool>.Success(false, "没有正在运行的会话任务"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CancelSessionRun failed for '{sessionId}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Stage 06 Agent ReAct 编排入口：后台 Task 运行 AgentOrchestrator，不阻塞 CEF 线程；
    /// 每 session 一个 CancellationTokenSource（复用 _activeSessionTokens，key 为 sessionId）。
    /// </summary>
}
