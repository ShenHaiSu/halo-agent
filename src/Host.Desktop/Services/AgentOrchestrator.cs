using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>
/// 单轮 Agent ReAct 执行结果。
/// </summary>
public sealed class AgentTurnResult
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public bool WasTruncated { get; init; }
    public int Iterations { get; init; }
    public int ToolExecutions { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AgentTurnResult Completed(int iterations, int toolExecutions) =>
        new() { Success = true, Iterations = iterations, ToolExecutions = toolExecutions };

    public static AgentTurnResult Truncated(int iterations, int toolExecutions) =>
        new() { Success = true, WasTruncated = true, Iterations = iterations, ToolExecutions = toolExecutions };

    public static AgentTurnResult Cancelled(int iterations, int toolExecutions) =>
        new() { Success = false, WasCancelled = true, Iterations = iterations, ToolExecutions = toolExecutions };

    public static AgentTurnResult Failed(string code, string message, int iterations = 0, int toolExecutions = 0) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message, Iterations = iterations, ToolExecutions = toolExecutions };
}

public interface IAgentOrchestrator
{
    int MaxIterations { get; }

    Task<AgentTurnResult> RunAgentTurnAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stage 06 Agent ReAct 多步循环编排引擎：
/// 用户输入 -&gt; 预算裁剪 -&gt; 双协议流式推理 -&gt; 工具调度执行 -&gt; 结果回写 JSONL -&gt; 下一轮，直至无 ToolCalls。
/// 工具参数瑕疵绝不抛异常中断，转自愈提示回传 LLM；支持 CancellationToken 取消并杀子进程。
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    public int MaxIterations { get; }

    private readonly IWorkspaceSessionService _sessionService;
    private readonly ITokenBudgetService _budgetService;
    private readonly ILlmGatewayClient _gatewayClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly StoragePathManager? _storageManager;
    private readonly ProviderConfigService? _providerConfigService;

    public AgentOrchestrator(
        IWorkspaceSessionService sessionService,
        ITokenBudgetService budgetService,
        ILlmGatewayClient gatewayClient,
        ToolRegistry? toolRegistry = null,
        StoragePathManager? storageManager = null,
        ProviderConfigService? providerConfigService = null,
        int maxIterations = 25)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _toolRegistry = toolRegistry ?? new ToolRegistry();
        _storageManager = storageManager;
        _providerConfigService = providerConfigService;
        MaxIterations = maxIterations > 0 ? maxIterations : 25;
    }

    public async Task<AgentTurnResult> RunAgentTurnAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        // 快速取消路径：<100ms 内响应取消，不做任何 IO
        if (cancellationToken.IsCancellationRequested)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-before-start" });
            return AgentTurnResult.Cancelled(0, 0);
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return AgentTurnResult.Failed("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");
        }

        if (string.IsNullOrWhiteSpace(userText))
        {
            return AgentTurnResult.Failed("EMPTY_PROMPT", "用户输入不能为空");
        }

        WorkspaceInfo? workspace;
        SessionMetadata? session;
        try
        {
            workspace = await _sessionService.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            session = await _sessionService.GetSessionAsync(workspaceId, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-during-load" });
            return AgentTurnResult.Cancelled(0, 0);
        }

        if (workspace == null)
        {
            return AgentTurnResult.Failed("WORKSPACE_NOT_FOUND", $"工作区不存在：'{workspaceId}'");
        }

        if (session == null)
        {
            return AgentTurnResult.Failed("SESSION_NOT_FOUND", $"会话不存在：'{sessionId}'");
        }

        // (1) 追加 UserMessage 至 messages.jsonl
        var userMessage = UnifiedChatMessage.User(userText.Trim());
        try
        {
            await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, userMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-during-append" });
            return AgentTurnResult.Cancelled(0, 0);
        }

        // 解析 Provider / Model（测试无配置时回退默认）
        var (provider, model) = ResolveProviderAndModel(providerId, modelId, session);

        string systemPrompt;
        try
        {
            systemPrompt = await SystemPromptBuilder.BuildAsync(workspace).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-during-prompt" });
            return AgentTurnResult.Cancelled(0, 0);
        }
        catch (Exception ex)
        {
            return AgentTurnResult.Failed("SYSTEM_PROMPT_ERROR", ex.Message);
        }

        string dataRoot = _storageManager?.CurrentDataRoot ?? string.Empty;
        int toolExecutions = 0;

        // (2) MaxIterations ReAct 循环
        for (int iteration = 1; iteration <= MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<UnifiedChatMessage> history;
            try
            {
                history = await _sessionService.GetSessionMessagesAsync(workspaceId, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                return AgentTurnResult.Cancelled(iteration - 1, toolExecutions);
            }

            List<UnifiedChatMessage> budgeted = _budgetService.TruncateToBudget(model, history);

            var requestMessages = new List<UnifiedChatMessage>(budgeted.Count + 1)
            {
                UnifiedChatMessage.System(systemPrompt)
            };
            requestMessages.AddRange(budgeted);

            string requestId = Guid.NewGuid().ToString("N");
            string sessionHash = !string.IsNullOrWhiteSpace(session.SessionHash)
                ? session.SessionHash
                : HeaderVariableResolver.ComputeSessionHash(workspaceId, sessionId);

            var headerContext = new HeaderResolutionContext(
                workspace.Id,
                workspace.Name ?? string.Empty,
                workspace.RootPath ?? string.Empty,
                sessionId,
                sessionHash,
                provider.Id,
                provider.Name,
                model.Id,
                requestId);

            var toolDefs = _toolRegistry.GetDefinitions()
                .Select(d => UnifiedToolDefinition.Create(d.Name, d.Description, d.ParametersJson))
                .ToList();

            var request = new UnifiedLlmRequest
            {
                RequestId = requestId,
                Provider = provider,
                Model = model,
                HeaderContext = headerContext,
                SystemInstructions = systemPrompt,
                Messages = requestMessages,
                Tools = toolDefs
            };

            // (5) 流式推理：汇总增量并实时推送
            var textAcc = new System.Text.StringBuilder();
            var reasoningAcc = new System.Text.StringBuilder();
            var pendingTools = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
            var toolOrder = new List<string>();
            UnifiedTokenUsage? usage = null;
            bool streamCancelled = false;
            string? streamErrorCode = null;
            string? streamErrorMessage = null;
            long ttftMark = Environment.TickCount64;
            bool ttftSent = false;

            PublishTurnEvent(sessionId, "IterationStarted", new { iteration, maxIterations = MaxIterations });

            try
            {
                await foreach (var evt in _gatewayClient.StreamAsync(request, cancellationToken).ConfigureAwait(false))
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
                                    PublishTurnEvent(sessionId, "FirstToken", new { iteration, ttftMs = Environment.TickCount64 - ttftMark });
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
                                PublishTurnEvent(sessionId, "ToolCallStarted", new
                                {
                                    iteration,
                                    callId = cid,
                                    functionName = pending.FunctionName
                                });
                            }
                            break;
                        case UnifiedStreamEventKind.ToolCallArgsDelta:
                            if (evt.ToolCall != null && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId) && evt.TextDelta != null)
                            {
                                if (pendingTools.TryGetValue(evt.ToolCall.CallId, out var p))
                                {
                                    p.ArgsBuilder.Append(evt.TextDelta);
                                }
                            }
                            break;
                        case UnifiedStreamEventKind.ToolCallCompleted:
                            if (evt.ToolCall != null && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId))
                            {
                                string cid = evt.ToolCall.CallId;
                                if (!pendingTools.TryGetValue(cid, out var done))
                                {
                                    done = new PendingToolCall { CallId = cid, ItemId = evt.ToolCall.ItemId };
                                    pendingTools[cid] = done;
                                    toolOrder.Add(cid);
                                }
                                if (!string.IsNullOrWhiteSpace(evt.ToolCall.FunctionName))
                                {
                                    done.FunctionName = evt.ToolCall.FunctionName;
                                }
                                if (!string.IsNullOrWhiteSpace(evt.ToolCall.ArgumentsJson))
                                {
                                    done.ArgsBuilder.Clear();
                                    done.ArgsBuilder.Append(evt.ToolCall.ArgumentsJson);
                                }
                            }
                            break;
                        case UnifiedStreamEventKind.UsageUpdated:
                            usage = evt.Usage;
                            break;
                        case UnifiedStreamEventKind.StreamCancelled:
                            streamCancelled = true;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                return AgentTurnResult.Cancelled(iteration - 1, toolExecutions);
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
                            reasoningAcc.Length > 0 ? reasoningAcc.ToString() : null);
                        await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, partial, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* 取消路径下持久化失败不掩盖取消语义 */ }
                }
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                return AgentTurnResult.Cancelled(iteration - 1, toolExecutions);
            }

            if (streamErrorCode != null)
            {
                try
                {
                    var errMsg = UnifiedChatMessage.Assistant($"本次推理遇到错误（{streamErrorCode}）：{streamErrorMessage}");
                    await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, errMsg, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore */ }
                PublishTurnEvent(sessionId, "TurnError", new { iteration, errorCode = streamErrorCode, errorMessage = streamErrorMessage });
                return AgentTurnResult.Failed(streamErrorCode, streamErrorMessage ?? string.Empty, iteration, toolExecutions);
            }

            // 汇总 AssistantMessage 并持久化
            var completedCalls = new List<UnifiedToolCall>();
            foreach (string cid in toolOrder)
            {
                var p = pendingTools[cid];
                if (string.IsNullOrWhiteSpace(p.FunctionName))
                {
                    continue;
                }
                completedCalls.Add(new UnifiedToolCall(cid, p.ItemId, p.FunctionName, p.ArgsBuilder.ToString()));
            }

            var assistantMessage = UnifiedChatMessage.Assistant(
                textAcc.Length > 0 ? textAcc.ToString() : null,
                completedCalls.Count > 0 ? completedCalls : null,
                reasoningAcc.Length > 0 ? reasoningAcc.ToString() : null);
            if (usage != null)
            {
                assistantMessage.InputTokens = usage.InputTokens;
                assistantMessage.OutputTokens = usage.OutputTokens;
                assistantMessage.CachedTokens = usage.CachedInputTokens;
            }

            try
            {
                await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, assistantMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                return AgentTurnResult.Cancelled(iteration, toolExecutions);
            }

            PublishTurnEvent(sessionId, "AssistantMessagePersisted", new
            {
                iteration,
                hasToolCalls = completedCalls.Count > 0,
                toolCallCount = completedCalls.Count,
                inputTokens = usage?.InputTokens ?? 0,
                outputTokens = usage?.OutputTokens ?? 0,
                cachedTokens = usage?.CachedInputTokens ?? 0
            });

            // (6) 无 ToolCalls 则 TurnCompleted 结束
            if (completedCalls.Count == 0)
            {
                PublishTurnEvent(sessionId, "TurnCompleted", new
                {
                    iterations = iteration,
                    toolExecutions,
                    protocol = model.Protocol == LlmProtocolType.Resp ? "resp" : "chat",
                    inputTokens = usage?.InputTokens ?? 0,
                    outputTokens = usage?.OutputTokens ?? 0,
                    cachedTokens = usage?.CachedInputTokens ?? 0
                });
                return AgentTurnResult.Completed(iteration, toolExecutions);
            }

            // 达到最大迭代仍有工具调用 → 截断暂停
            if (iteration >= MaxIterations)
            {
                try
                {
                    var notice = UnifiedChatMessage.Assistant(
                        $"已达到单轮最大迭代次数（{MaxIterations}），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。");
                    await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, notice, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore */ }
                PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = iteration, toolExecutions, maxIterations = MaxIterations });
                return AgentTurnResult.Truncated(iteration, toolExecutions);
            }

            // (7) 依次调度执行每个 ToolCall（自愈：绝不抛异常中断）
            foreach (var call in completedCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PublishTurnEvent(sessionId, "ToolExecutionStarted", new
                {
                    iteration,
                    callId = call.CallId,
                    functionName = call.FunctionName
                });

                ToolResult toolResult;
                try
                {
                    var dto = new ToolCallDto(call.CallId, call.FunctionName, call.ArgumentsJson);
                    var toolContext = new ToolContext
                    {
                        WorkspaceRoot = workspace.RootPath ?? string.Empty,
                        WorkspaceId = workspaceId,
                        SessionId = sessionId,
                        DataRoot = dataRoot,
                        CancellationToken = cancellationToken
                    };
                    toolResult = await _toolRegistry.DispatchAsync(dto, toolContext, cancellationToken).ConfigureAwait(false);
                    toolResult ??= ToolResult.Fail($"工具 '{call.FunctionName}' 返回空结果，请重试。");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    return AgentTurnResult.Cancelled(iteration, toolExecutions);
                }
                catch (Exception ex)
                {
                    // 最后一道防线：任何意外异常都转自愈提示，绝不中断循环
                    toolResult = ToolResult.Fail($"工具 '{call.FunctionName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。");
                }

                toolExecutions++;

                string toolText = toolResult.Success
                    ? (string.IsNullOrEmpty(toolResult.Output) ? "(工具执行成功，无输出)" : toolResult.Output)
                    : $"[Tool Error - {call.FunctionName}]: {toolResult.Error}"
                      + (string.IsNullOrEmpty(toolResult.Output) ? string.Empty : $"\nOutput: {toolResult.Output}")
                      + HintForTool(call.FunctionName);

                var toolMessage = UnifiedChatMessage.Tool(call.CallId, toolText, call.FunctionName);

                try
                {
                    await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, toolMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    return AgentTurnResult.Cancelled(iteration, toolExecutions);
                }

                PublishTurnEvent(sessionId, "ToolExecutionCompleted", new
                {
                    iteration,
                    callId = call.CallId,
                    functionName = call.FunctionName,
                    success = toolResult.Success,
                    exitCode = toolResult.ExitCode,
                    durationMs = toolResult.DurationMs
                });
            }
        }

        // 理论不可达（循环内 iteration>=MaxIterations 即返回），兜底截断
        PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = MaxIterations, toolExecutions, maxIterations = MaxIterations });
        return AgentTurnResult.Truncated(MaxIterations, toolExecutions);
    }

    private (ProviderConfig Provider, ModelConfig Model) ResolveProviderAndModel(
        string? providerId, string? modelId, SessionMetadata session)
    {
        ProviderConfig? provider = null;
        if (_providerConfigService != null)
        {
            try
            {
                string pid = !string.IsNullOrWhiteSpace(providerId)
                    ? providerId!
                    : (session.ProviderId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pid))
                {
                    provider = _providerConfigService.GetProviderById(pid);
                }
                if (provider == null)
                {
                    var doc = _providerConfigService.GetProvidersConfigInternal();
                    provider = doc.Providers.FirstOrDefault(p => p.Enabled)
                        ?? doc.Providers.FirstOrDefault();
                }
            }
            catch
            {
                provider = null;
            }
        }

        provider ??= new ProviderConfig
        {
            Id = string.IsNullOrWhiteSpace(providerId) ? "provider_default" : providerId!,
            Name = "DefaultProvider",
            BaseUrl = "https://api.openai.com/v1"
        };

        string mid = !string.IsNullOrWhiteSpace(modelId) ? modelId! : (session.ModelId ?? string.Empty);
        ModelConfig? model = null;
        if (!string.IsNullOrWhiteSpace(mid))
        {
            model = provider.Models.FirstOrDefault(m => string.Equals(m.Id, mid, StringComparison.OrdinalIgnoreCase));
        }
        model ??= provider.Models.FirstOrDefault(m => m.Enabled)
            ?? provider.Models.FirstOrDefault()
            ?? new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat, MaxContextTokens = 128000, MaxOutputTokens = 4096 };

        return (provider, model);
    }

    private static string HintForTool(string functionName)
    {
        return functionName switch
        {
            "edit_file" => " Hint: Call read_file first to inspect the exact lines before retrying edit_file.",
            "write_file" => " Hint: Verify the parent directory exists (list_dir) and check path spelling before retrying.",
            "read_file" => " Hint: Use list_dir to confirm the file exists and check relative path spelling.",
            "exec_command" => " Hint: Prefer runner cmd with standard cmd.exe syntax; avoid PowerShell-only syntax.",
            "list_dir" => " Hint: Verify the directory path is relative to the workspace root.",
            "grep_search" => " Hint: Check the search pattern and target directory, then retry with a simpler pattern.",
            _ => " Hint: Check tool arguments and retry."
        };
    }

    private static void PublishTurnEvent(string sessionId, string kind, object payload)
    {
        try
        {
            JsEventDispatcher.Publish("agent:turn-event", new
            {
                sessionId,
                kind,
                payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        catch
        {
            // 事件推送永不破坏 ReAct 主循环
        }
    }

    private static void PublishStreamDelta(string sessionId, string kind, string textDelta)
    {
        try
        {
            var dto = SessionStreamEventDto.FromUnifiedEvent(
                sessionId,
                kind == "ReasoningDelta"
                    ? UnifiedStreamEvent.Reasoning(textDelta)
                    : UnifiedStreamEvent.Text(textDelta));
            JsEventDispatcher.Publish("llm:stream-event", dto);
        }
        catch
        {
            // 事件推送永不破坏 ReAct 主循环
        }
    }

    private sealed class PendingToolCall
    {
        public string CallId { get; init; } = string.Empty;
        public string? ItemId { get; set; }
        public string FunctionName { get; set; } = string.Empty;
        public System.Text.StringBuilder ArgsBuilder { get; } = new();
    }
}
