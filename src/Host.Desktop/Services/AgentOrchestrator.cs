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
     /// <summary>need08：本轮解析出的有效轮次上限（0=无限）。构造值保持兼容，老属性不动。</summary>
     public int ResolvedMaxIterations { get; private set; }
     private readonly IWorkspaceSessionService _sessionService;
     private readonly ITokenBudgetService _budgetService;
     private readonly ILlmGatewayClient _gatewayClient;
     private readonly ToolRegistry _toolRegistry;
     private readonly StoragePathManager? _storageManager;
     private readonly ProviderConfigService? _providerConfigService;
     private readonly PromptConfigService? _promptConfigService;
     private readonly AgentConfigService? _agentConfigService;
     public AgentOrchestrator(IWorkspaceSessionService sessionService, ITokenBudgetService budgetService, ILlmGatewayClient gatewayClient, ToolRegistry? toolRegistry = null, StoragePathManager? storageManager = null, ProviderConfigService? providerConfigService = null, PromptConfigService? promptConfigService = null, int maxIterations = 25, AgentConfigService? agentConfigService = null)
     {
         _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
         _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
         _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
         _toolRegistry = toolRegistry ?? new ToolRegistry();
         _storageManager = storageManager;
         _providerConfigService = providerConfigService;
         _promptConfigService = promptConfigService;
         _agentConfigService = agentConfigService;
         MaxIterations = maxIterations > 0 ? maxIterations : 25;
         ResolvedMaxIterations = MaxIterations;
     }
     public async Task<AgentTurnResult> RunAgentTurnAsync(string workspaceId, string sessionId, string userText, string? providerId = null, string? modelId = null, CancellationToken cancellationToken = default)
     {
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
        // need09-05：取本会话暂存粘贴图（Take 语义，失败 Warn 后文字仍发送；引用随 userMessage 落盘，能力位只影响发送剥离）
        try
        {
            var pendingImgs = HostBridgeFacade.PendingSessionImages.Take(sessionId);
            if (pendingImgs != null && pendingImgs.Count > 0) userMessage.Images = pendingImgs;
        }
        catch (Exception exImgs)
        {
            Infrastructure.Logging.HaloLogger.App.Warn("[Image] 附件暂存取出失败，已降级为纯文本发送", exImgs);
        }
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
        // need09-01/02/03：轮首快照（Skill/Memory L0 块 + 模型视觉能力；只读一次，写操作下一轮生效；失败回默认不断会话）
        string? skillIndexBlock = null;
        string? memoryPinnedBlock = null;
        int residentStableTokens = 0;
        bool snapVision = model.SupportsVision;
        int snapMaxImages = VisionDefaults.ResolveMaxImages(model.MaxImagesPerTurn);
        try
        {
            if (_storageManager != null)
            {
                var skillMemory = new SkillMemoryService(_storageManager);
                var merged = skillMemory.GetMergedForPrompt(workspace.RootPath);
                var skillEntries = merged.GlobalSkills.Concat(merged.ProjectSkills)
                    .Where(e => e != null && e.Enabled)
                    .Select(e => new SystemPromptBuilder.StableSkillEntry(e.Scope, e.Name, e.Summary, e.Priority));
                skillIndexBlock = SystemPromptBuilder.BuildSkillIndexBlock(skillEntries);
                if (merged.GlobalSkills.Concat(merged.ProjectSkills).Any(e => e != null && e.Enabled && string.Equals(e.Pin, "full", StringComparison.OrdinalIgnoreCase)))
                    Infrastructure.Logging.HaloLogger.App.Warn("[Skill] 存在 pin=full 的 Skill，本期仅渲染索引行（正文按需经 skill_tool 拉取）");
                var memEntries = merged.GlobalMemory.Concat(merged.ProjectMemory)
                    .Where(e => e != null && e.Enabled && !string.Equals(e.Pin, "archived", StringComparison.OrdinalIgnoreCase))
                    .Select(e => new SystemPromptBuilder.StableMemoryEntry(e.Scope, e.Name, e.BodyMarkdown, e.Priority));
                memoryPinnedBlock = SystemPromptBuilder.BuildMemoryPinnedBlock(memEntries);
                if (!string.IsNullOrEmpty(skillIndexBlock)) residentStableTokens += TokenBudgetService.EstimateTokens(skillIndexBlock);
                if (!string.IsNullOrEmpty(memoryPinnedBlock)) residentStableTokens += TokenBudgetService.EstimateTokens(memoryPinnedBlock);
            }
        }
        catch (Exception exSnap)
        {
            Infrastructure.Logging.HaloLogger.App.Warn("[Skill] Skill/Memory 快照失败，已回退空域继续会话", exSnap);
            skillIndexBlock = null;
            memoryPinnedBlock = null;
            residentStableTokens = 0;
        }

         string systemPrompt;
         try
         {
             var promptCfg = _promptConfigService?.GetPromptConfigInternal() ?? PromptDefaults.CreateDefault();
             var preview = SystemPromptBuilder.BuildPreview(promptCfg, workspace, pyOverride: null, dateOverride: null, skillIndexBlock: skillIndexBlock, memoryPinnedBlock: memoryPinnedBlock);
             systemPrompt = preview.AssembledText;
         }
         catch (OperationCanceledException)
         {
             PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-during-prompt" });
             return AgentTurnResult.Cancelled(0, 0);
         }
         catch (Exception ex)
         {
             try { systemPrompt = await SystemPromptBuilder.BuildAsync(workspace).ConfigureAwait(false); }
             catch (Exception ex2) { return AgentTurnResult.Failed("SYSTEM_PROMPT_ERROR", ex2.Message); }
             Infrastructure.Logging.HaloLogger.App.Warn("[Prompt] 配置渲染失败，已回退默认模板", ex);
         }

        string dataRoot = _storageManager?.CurrentDataRoot ?? string.Empty;
        int toolExecutions = 0;
        // need05-01：本轮账单累计 Σ（含工具循环的多 call；单 call 轮即等于 usage）。失败/取消分支直接返回不发布，不计入。
        long turnBilledIn = 0;
        long turnBilledOut = 0;
        // need08：轮首快照（追加 user 之后、循环之前，只读一次；保存不中断本轮，下一轮生效）
        int effectiveMax = MaxIterations;
        int cfgExecDefaultMs = AgentDefaults.DefaultTimeoutMs;
        int cfgExecMaxMs = AgentDefaults.MaxTimeoutMs;
        try
        {
            if (_agentConfigService != null)
            {
                var cfg = _agentConfigService.GetInternal();
                cfgExecDefaultMs = cfg.ExecDefaultTimeoutMs;
                cfgExecMaxMs = cfg.ExecMaxTimeoutMs;
                if (cfg.MaxIterations == 0)
                {
                    effectiveMax = 0; // 无限轮次
                }
                else if (cfg.MaxIterations > 0)
                {
                    effectiveMax = Math.Min(cfg.MaxIterations, AgentDefaults.HardIterationsCeil);
                }
                else
                {
                    Infrastructure.Logging.HaloLogger.App.Warn($"[Agent] agent.json 轮次非法（{cfg.MaxIterations}），已回退构造值 {MaxIterations}");
                    effectiveMax = MaxIterations;
                }
            }
        }
        catch (Exception ex)
        {
            Infrastructure.Logging.HaloLogger.App.Warn("[Agent] 智能体行为配置读取失败，已回退构造值", ex);
            effectiveMax = MaxIterations;
        }
        ResolvedMaxIterations = effectiveMax;
        if (effectiveMax == 0)
        {
            Infrastructure.Logging.HaloLogger.App.Warn($"[Agent] infinite iterations for session {sessionId}，仅取消/模型停手可结束");
        }
        // (2) ReAct 循环（need08：effectiveMax<=0 即无限轮次，永不进截断分支）
        for (int iteration = 1; effectiveMax <= 0 || iteration <= effectiveMax; iteration++)
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

            // need05-02：budgeted 已是稳定前缀（平时全量、超 92% 高水位才块裁）；requestMessages 顺序即 [system.stable+ephemeral]+历史，不再调序。
            // need09-02：常驻 L0 计入 usedInput 分子（自然推高水位）。
            List<UnifiedChatMessage> budgeted = _budgetService.TruncateToBudget(model, history, 1024, residentStableTokens);

             var requestMessages = new List<UnifiedChatMessage>(budgeted.Count + 1);
             if (!string.IsNullOrWhiteSpace(systemPrompt)) requestMessages.Add(UnifiedChatMessage.System(systemPrompt));
             requestMessages.AddRange(budgeted);
            // need09-03：非视觉模型含图 → 剥离图片 + 通知 + 纯文本降级（仅一次；历史落盘不受影响，切换模型可恢复）
            if (!snapVision && requestMessages.Any(m => m.Images != null && m.Images.Count > 0))
            {
                int stripped = requestMessages.Where(m => m.Images != null).Sum(m => m.Images!.Count);
                foreach (var m in requestMessages) m.Images = null;
                var notice = UnifiedChatMessage.Assistant($"[图片已忽略] 当前模型 {model.Id} 不支持看图，本轮 {stripped} 张图片已剥离，文字照常发送。");
                try { await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, notice, cancellationToken).ConfigureAwait(false); }
                catch { /* 通知落盘失败不中断本轮 */ }
            }
            else if (snapVision && snapMaxImages > 0)
            {
                // need09-03：超 MaxImagesPerTurn 取最新 N 张（保 latest 丢最旧），前后端网关一致，Adapter 侧仍有兜底
                int totalImgs = requestMessages.Where(m => m.Images != null).Sum(m => m.Images!.Count);
                if (totalImgs > snapMaxImages)
                {
                    var keepSet = new HashSet<ChatImageRef>();
                    for (int ri = requestMessages.Count - 1; ri >= 0 && keepSet.Count < snapMaxImages; ri--)
                    {
                        var imgs = requestMessages[ri].Images;
                        if (imgs == null) continue;
                        for (int ki = imgs.Count - 1; ki >= 0 && keepSet.Count < snapMaxImages; ki--) keepSet.Add(imgs[ki]);
                    }
                    foreach (var m in requestMessages)
                    {
                        if (m.Images == null) continue;
                        var kept = m.Images.Where(r => keepSet.Contains(r)).ToList();
                        m.Images = kept.Count == 0 ? null : kept;
                    }
                    Infrastructure.Logging.HaloLogger.App.Warn($"[Image] 本轮图片 {totalImgs} 张超限（上限 {snapMaxImages}），已保留最新 {snapMaxImages} 张");
                }
            }
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

            PublishTurnEvent(sessionId, "IterationStarted", new { iteration, maxIterations = effectiveMax });

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
                                string cid2 = evt.ToolCall.CallId;
                                if (!pendingTools.TryGetValue(cid2, out var done2))
                                {
                                    done2 = new PendingToolCall { CallId = cid2, ItemId = evt.ToolCall.ItemId };
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
                                }
                                // P1：把完整参数实时推送给前端，转入对应会话桶，避免卡片参数区永久为空
                                PublishTurnEvent(sessionId, "ToolCallUpdated", new
                                {
                                    iteration,
                                    callId = cid2,
                                    functionName = done2.FunctionName,
                                    argsJson = done2.ArgsBuilder.ToString()
                                });
                            }
                            break;
                        case UnifiedStreamEventKind.UsageUpdated:
                            usage = evt.Usage;
                            // need05-01：账单累计（本轮内多次 LLM 调用的 Σ；streamError/cancel 分支直接返回，此处不回滚但不发布）
                            if (evt.Usage != null)
                            {
                                turnBilledIn += evt.Usage.InputTokens;
                                turnBilledOut += evt.Usage.OutputTokens;
                            }
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

            // need05-01：inputTokens 仍是末 call 占用（不是 Σ，避免重蹈 04-05 膨胀）；billed* 为本轮多次调用的 Σ。
            PublishTurnEvent(sessionId, "AssistantMessagePersisted", new
            {
                iteration,
                hasToolCalls = completedCalls.Count > 0,
                toolCallCount = completedCalls.Count,
                inputTokens = usage?.InputTokens ?? 0,
                outputTokens = usage?.OutputTokens ?? 0,
                cachedTokens = usage?.CachedInputTokens ?? 0,
                billedPrompt = turnBilledIn,
                billedCompletion = turnBilledOut,
                updatedAtUtc = DateTime.UtcNow.ToString("o")
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
                    cachedTokens = usage?.CachedInputTokens ?? 0,
                    billedPrompt = turnBilledIn,
                    billedCompletion = turnBilledOut,
                    updatedAtUtc = DateTime.UtcNow.ToString("o")
                });
                return AgentTurnResult.Completed(iteration, toolExecutions);
            }

            // 达到最大迭代仍有工具调用 → 截断暂停（need08：无限模式 effectiveMax<=0 永不进）
            if (effectiveMax > 0 && iteration >= effectiveMax)
            {
                try
                {
                    var notice = UnifiedChatMessage.Assistant(
                        $"已达到单轮最大迭代次数（{effectiveMax}），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。");
                    await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, notice, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore */ }
                PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = iteration, toolExecutions, maxIterations = effectiveMax });
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
                    functionName = call.FunctionName,
                    argsJson = call.ArgumentsJson
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
                        CancellationToken = cancellationToken,
                        ExecDefaultTimeoutMsOverride = cfgExecDefaultMs,
                        ExecMaxTimeoutMsOverride = cfgExecMaxMs,
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
                    durationMs = toolResult.DurationMs,
                    argsJson = call.ArgumentsJson,
                    resultText = TruncateForEvent(toolText, 8000),
                    updatedAtUtc = DateTime.UtcNow.ToString("o")
                });
            }
        }

        // 理论不可达（有限循环内 iteration>=effectiveMax 即返回；无限循环内必 return），兜底截断
        PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = effectiveMax, toolExecutions, maxIterations = effectiveMax });
        return AgentTurnResult.Truncated(effectiveMax, toolExecutions);
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
    private static string TruncateForEvent(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length > max ? s.Substring(0, max) + "\n...（输出过长已截断）" : s;
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
