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
    private readonly UsageMetricsService? _usageMetrics;
    private readonly SubAgentCoordinator? _coordinator;
    /// <summary>need13：本实例角色 main|sub（子实例由 Coordinator 构造时传入 sub）。</summary>
    public string AgentRole { get; }
    /// <summary>need13：本实例轮首策略快照（子实例构造时传入；主实例每轮刷新）。</summary>
    public SubPolicySnapshot SubPolicy { get; private set; } = new();
    public AgentOrchestrator(IWorkspaceSessionService sessionService, ITokenBudgetService budgetService, ILlmGatewayClient gatewayClient, ToolRegistry? toolRegistry = null, StoragePathManager? storageManager = null, ProviderConfigService? providerConfigService = null, PromptConfigService? promptConfigService = null, int maxIterations = 25, AgentConfigService? agentConfigService = null, UsageMetricsService? usageMetrics = null, SubAgentCoordinator? coordinator = null, SubPolicySnapshot? subPolicy = null, string agentRole = "main")
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _toolRegistry = toolRegistry ?? new ToolRegistry();
        _storageManager = storageManager;
        _providerConfigService = providerConfigService;
        _promptConfigService = promptConfigService;
        _agentConfigService = agentConfigService;
        _usageMetrics = usageMetrics ?? (_storageManager != null ? new UsageMetricsService(_storageManager) : null);
        _coordinator = coordinator;
        AgentRole = string.Equals(agentRole, "sub", StringComparison.OrdinalIgnoreCase) ? "sub" : "main";
        if (subPolicy != null) SubPolicy = subPolicy;
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
         // 轮前兜底：上一轮若死在“assistant 已落盘、tool 回包未落盘”之间，历史尾部就是孤儿 tool_calls，
         // 直接发模型必 400。先愈合（补 synthetic cancelled tool），再追加本轮 user 消息。
         try
         {
             await _sessionService.HealSessionHistoryAsync(workspaceId, sessionId, CancellationToken.None).ConfigureAwait(false);
         }
         catch { /* 愈合失败不阻塞本轮，网关错误会正常上报 */ }

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
         // need13：轮首策略快照（本轮内所有 spawn/skill/memory 判定共用它；barrier 等待中变更不影响本轮）
         if (string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
         {
             // 子实例：构造时已带快照，不再读服务
         }
         else if (_agentConfigService != null)
         {
             try { SubPolicy = new SubPolicySnapshot(_agentConfigService.GetInternal()); } catch { }
         }
         // need13：主 registry 常显 spawn（轮首按快照刷新 description 后缀；关闭仍可见、调用强拒绝）
         try { if (!string.Equals(AgentRole, "sub", System.StringComparison.OrdinalIgnoreCase)) _toolRegistry.Register(new Host.Desktop.Tools.BuiltIn.SpawnSubAgentTool(SubPolicy.SubAgentEnabled)); } catch { }
         // need13：Prompt 预声明（主策略块稳定区尾部；子后缀块仅 sub）
         string subPolicyBlock = SystemPromptBuilder.BuildSubAgentPolicyBlock(
             SubPolicy.SubAgentEnabled, SubPolicy.SubMaxParallel, SubPolicy.SubMaxIterations,
             SubPolicy.SubAllowExecCommand, SubPolicy.SubAllowFileWrite,
             SubPolicy.SubAllowSkillWrite, SubPolicy.SubAllowMemoryWrite);
         // need16：轮首 Goal 快照（失败回空不断会话；本轮内写入下一轮生效）
         string? goalBlock = null;
         try
         {
             if (_storageManager != null)
             {
                 string mainId = sessionId;
                 try { if (session != null && !string.IsNullOrWhiteSpace(session.ParentSessionId)) mainId = session.ParentSessionId!; } catch { }
                 var goalSvc = new GoalService(_storageManager);
                 var (g, _) = goalSvc.TryLoadGoal(workspaceId, mainId);
                 goalBlock = SystemPromptBuilder.BuildGoalBlock(g);
                 if (string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(goalBlock))
                     goalBlock += "\n（你是子代理：仅可 goal_tool/get 查看，不可写入。）";
             }
         }
         catch { goalBlock = null; }
         try
         {
             var promptCfg = _promptConfigService?.GetPromptConfigInternal() ?? PromptDefaults.CreateDefault();
             var preview = SystemPromptBuilder.BuildPreview(promptCfg, workspace, pyOverride: null, dateOverride: null, skillIndexBlock: skillIndexBlock, memoryPinnedBlock: memoryPinnedBlock, goalBlock: goalBlock);
             systemPrompt = preview.AssembledText;
             if (!string.IsNullOrWhiteSpace(subPolicyBlock))
                 systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? subPolicyBlock + "\n" : systemPrompt + "\n" + subPolicyBlock + "\n";
             if (string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
                 systemPrompt += "\n" + SystemPromptBuilder.SubAgentSuffixBlock + "\n";
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
         // need12：轮内用量行收集（每次 HTTP 请求一行，轮末单事务批量写库）。
         var usageRows = new List<PendingUsageRow>();
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
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0：循环入口即取消，保住前面 iteration 已落袋行。
                UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                return AgentTurnResult.Cancelled(iteration - 1, toolExecutions);
            }
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
             // need11-01：读盘→dataURL 本轮内存缓存（重启/多会话安全，无全局锁；null 也缓存避免重复读丢失文件）
             var imgCache = new Dictionary<string, string?>(StringComparer.Ordinal);
             Func<ChatImageRef, string?> imgResolver = img =>
             {
                 if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
                 string key = img.ImageId.Trim();
                 if (imgCache.TryGetValue(key, out var hit)) return hit;
                 string? url = ImageAttachmentStore.ResolveDataUrl(dataRoot, workspaceId, sessionId, img);
                 imgCache[key] = url;
                 return url;
             };
             var request = new UnifiedLlmRequest
             {
                 RequestId = requestId,
                 Provider = provider,
                 Model = model,
                 HeaderContext = headerContext,
                 SystemInstructions = systemPrompt,
                 Messages = requestMessages,
                 Tools = toolDefs,
                 ImageResolver = imgResolver,
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

            PublishTurnEvent(sessionId, "IterationStarted", new { iteration, maxIterations = effectiveMax, agentRole = AgentRole, subEnabled = SubPolicy.SubAgentEnabled, subMaxParallel = SubPolicy.SubMaxParallel, subMaxIterations = SubPolicy.SubMaxIterations });

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
                                      functionName = pending.FunctionName,
                                      receivedBytes = 0L
                                  });
                             }
                             break;
                         case UnifiedStreamEventKind.ToolCallArgsDelta:
                             if (evt.ToolCall != null && !string.IsNullOrWhiteSpace(evt.ToolCall.CallId) && evt.TextDelta != null)
                             {
                                 if (pendingTools.TryGetValue(evt.ToolCall.CallId, out var p))
                                 {
                                     p.ArgsBuilder.Append(evt.TextDelta);
                                     try { p.ReceivedBytes += System.Text.Encoding.UTF8.GetByteCount(evt.TextDelta); }
                                     catch { p.ReceivedBytes = p.ArgsBuilder.Length; }
                                    bool isFileWrite = p.FunctionName is "write_file" or "edit_file" or "edit_lines";
                                     long now = Environment.TickCount64;
                                     bool throttleHit = (now - p.LastPublishTick) >= 150 || evt.TextDelta.Length > 4096;
                                     if (isFileWrite ? throttleHit : true)
                                     {
                                         p.LastPublishTick = now; p.LastPublishedBytes = p.ReceivedBytes;
                                         PublishTurnEvent(sessionId, "ToolCallUpdated", new
                                         {
                                             iteration,
                                             callId = evt.ToolCall.CallId,
                                             functionName = p.FunctionName,
                                             argsDelta = evt.TextDelta,
                                             receivedBytes = p.ReceivedBytes,
                                             argsLength = p.ArgsBuilder.Length
                                         });
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
                                     try { done2.ReceivedBytes = System.Text.Encoding.UTF8.GetByteCount(evt.ToolCall.ArgumentsJson); } catch { }
                                 }
                                 // P1：把完整参数实时推送给前端，转入对应会话桶，避免卡片参数区永久为空
                                 PublishTurnEvent(sessionId, "ToolCallUpdated", new
                                 {
                                     iteration,
                                     callId = cid2,
                                     functionName = done2.FunctionName,
                                     argsJson = done2.ArgsBuilder.ToString(),
                                     receivedBytes = done2.ReceivedBytes,
                                     argsLength = done2.ArgsBuilder.Length
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
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0/P2：流内抛取消同样先落袋本轮（有实测用实测，无实测用估算），再保住前面 iteration。
                UsageRecorder.CollectAuto(usageRows, provider, model, usage,
                    UsageRecorder.EstimateInput(requestMessages, systemPrompt),
                    UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                    "cancelled", requestId, sessionId, workspaceId, UsageRecorder.SourceAgent);
                UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
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
                // need12-P2：有实测用实测，无实测用估算（input 全量 + 已产出文本），保证取消不低估。
                UsageRecorder.CollectAuto(usageRows, provider, model, usage,
                    UsageRecorder.EstimateInput(requestMessages, systemPrompt),
                    UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                    "cancelled", requestId, sessionId, workspaceId, UsageRecorder.SourceAgent);
                UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
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
                // need12-P2：错误同样智能落袋（有实测用实测，无实测用估算）。
                UsageRecorder.CollectAuto(usageRows, provider, model, usage,
                    UsageRecorder.EstimateInput(requestMessages, systemPrompt),
                    UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                    "error", requestId, sessionId, workspaceId, UsageRecorder.SourceAgent);
                UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
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
             // need12：本 iteration 的 HTTP 请求落袋（纯内存，轮末统一写库；失败/取消分支在各自 return 前同样落袋）。
             // need12-P2：无 usage 实测时退化为 degraded 估算行（input 全量估算，output 用已产出文本估算）。
             UsageRecorder.CollectAuto(usageRows, provider, model, usage,
                 UsageRecorder.EstimateInput(requestMessages, systemPrompt),
                 UsageRecorder.EstimateOutput(textAcc, reasoningAcc),
                 usage != null ? "ok" : "degraded", requestId, sessionId, workspaceId, UsageRecorder.SourceAgent);

            try
            {
                await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, assistantMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                // need12-P0：assistant 落盘时被取消，本 iteration 已落袋，保住前面行。
                UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                return AgentTurnResult.Cancelled(iteration, toolExecutions);
            }
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
                 // need13：主 TurnCompleted 透出子耗 rollup（供顶栏“本轮含子耗”）
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
                     subAgents = new { count = 0, billedPrompt = 0L, billedCompletion = 0L },
                     updatedAtUtc = DateTime.UtcNow.ToString("o")
                 });
                 FlushUsageRows(usageRows, sessionId, workspaceId);
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
                 FlushUsageRows(usageRows, sessionId, workspaceId);
                 return AgentTurnResult.Truncated(iteration, toolExecutions);
            }

            // (7) 依次调度执行每个 ToolCall（自愈：绝不抛异常中断）
            // need13：spawn 分流（normal 先串行，spawn 走 Coordinator Barrier；等待不消费主 iteration）
            var spawnCalls = completedCalls.Where(c => string.Equals(c.FunctionName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)).ToList();
            var normalCalls = completedCalls.Where(c => !string.Equals(c.FunctionName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var call in normalCalls)
            {
                try { cancellationToken.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    // need12-P0：工具执行前被取消，本 iteration 已落袋，保住前面行。
                    UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                    return AgentTurnResult.Cancelled(iteration, toolExecutions);
                }

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
                        AgentRole = AgentRole,
                        ParentSessionId = string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase) ? (session.ParentSessionId ?? string.Empty) : string.Empty,
                        SubAgentEnabled = SubPolicy.SubAgentEnabled,
                        SubMaxParallel = SubPolicy.SubMaxParallel,
                        SubMaxIterations = SubPolicy.SubMaxIterations,
                        SubAllowExecCommand = SubPolicy.SubAllowExecCommand,
                        SubAllowFileWrite = SubPolicy.SubAllowFileWrite,
                        SubAllowSkillWrite = SubPolicy.SubAllowSkillWrite,
                        SubAllowMemoryWrite = SubPolicy.SubAllowMemoryWrite,
                        SkillMemoryScopeDefault = string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase) ? "project" : "project",
                    };
                    toolResult = await _toolRegistry.DispatchAsync(dto, toolContext, cancellationToken).ConfigureAwait(false);
                    toolResult ??= ToolResult.Fail($"工具 '{call.FunctionName}' 返回空结果，请重试。");
                }
                catch (OperationCanceledException)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    // need12-P0：工具执行中被取消，本 iteration 已落袋，保住前面行。
                    UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                    return AgentTurnResult.Cancelled(iteration, toolExecutions);
                }
                catch (Exception ex)
                {
                    // 最后一道防线：任何意外异常都转自愈提示，绝不中断循环
                    toolResult = ToolResult.Fail($"工具 '{call.FunctionName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。");
                }

                toolExecutions++;

                 string errorCode = toolResult.ErrorCode ?? ToolResult.TryExtractCode(toolResult.Error) ?? string.Empty;
                 string toolText = toolResult.Success
                     ? (string.IsNullOrEmpty(toolResult.Output) ? "(工具执行成功，无输出)" : toolResult.Output)
                     : $"[Tool Error - {call.FunctionName}]: {toolResult.Error}"
                       + (string.IsNullOrEmpty(toolResult.Output) ? string.Empty : $"\nOutput: {toolResult.Output}")
                       + HintForTool(call.FunctionName, errorCode)
                       + BuildNextActionHint(toolResult, call.FunctionName, call.ArgumentsJson, errorCode);

                 var toolMessage = UnifiedChatMessage.Tool(call.CallId, toolText, call.FunctionName);
                 // need11-fix: view_image 等工具返回的 Images 必须挂到 tool 消息上，否则下一轮 PlanTurn 读不到，静默丢图（仅文本上游）。
                 if (toolResult.Success && toolResult.Images != null && toolResult.Images.Count > 0)
                 {
                     toolMessage.Images = new List<Host.Desktop.Core.Models.ChatImageRef>(toolResult.Images);
                 }

                try
                {
                    await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, toolMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                    // need12-P0：tool 落盘时被取消，本 iteration 已落袋，保住前面行。
                    UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
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
                     fileBytes = toolResult.FileBytes,
                     updatedAtUtc = DateTime.UtcNow.ToString("o")
                 });
            }
            // need13：spawn 分流（主才允许；子调 spawn 由 registry 禁用桩直接拒，不会走到这里——此处为纵深兜底）
            if (spawnCalls.Count > 0)
            {
                if (!string.Equals(AgentRole, "main", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in spawnCalls)
                    {
                        string fail = "[SUBAGENT_NESTED_DENIED] 子代理内禁止再 spawn（深度=1）。本次已拒绝。请在当前子内串行完成，必要时记入总结交主代理拆分。";
                        await AppendToolTextAsync(workspaceId, sessionId, s.CallId, "spawn_subagent", fail, cancellationToken).ConfigureAwait(false);
                        toolExecutions++;
                    }
                }
                else if (!SubPolicy.SubAgentEnabled)
                {
                    foreach (var s in spawnCalls)
                    {
                        string fail = "[SUBAGENT_DISABLED] 子代理已关闭（设置>智能体行为>子代理>总开关=false）。本次 spawn 已拒绝，未创建子会话。请不要重试 spawn，直接用主代理能力继续，并在回复中告知用户可在设置中重新开启。"
                            + HintForTool("spawn_subagent", "SUBAGENT_DISABLED");
                        await AppendToolTextAsync(workspaceId, sessionId, s.CallId, "spawn_subagent", fail, cancellationToken).ConfigureAwait(false);
                        toolExecutions++;
                    }
                }
                else
                {
                    var specs = new List<SpawnCallSpec>();
                    foreach (var s in spawnCalls)
                    {
                        var (spec, vfail, _) = SubAgentCoordinator.ParseSpawnCall(s.CallId, s.ArgumentsJson);
                        if (spec == null)
                        {
                            string fail = (vfail ?? "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。") + HintForTool("spawn_subagent", "SUBAGENT_VALIDATION");
                            await AppendToolTextAsync(workspaceId, sessionId, s.CallId, "spawn_subagent", fail, cancellationToken).ConfigureAwait(false);
                            toolExecutions++;
                        }
                        else specs.Add(spec);
                    }
                    if (specs.Count > 0)
                    {
                        var coord = _coordinator ?? new SubAgentCoordinator(_sessionService, _budgetService, _gatewayClient, _storageManager, _providerConfigService, _promptConfigService, _agentConfigService, _usageMetrics);
                        var (handles, rejected) = await coord.CreateSubsAsync(workspaceId, sessionId, specs, session.ProviderId ?? string.Empty, session.ModelId ?? string.Empty, SubPolicy, cancellationToken).ConfigureAwait(false);
                        foreach (var r in rejected)
                        {
                            string fail = r.FailText + HintForTool("spawn_subagent", "SUBAGENT_QUOTA_EXCEEDED");
                            await AppendToolTextAsync(workspaceId, sessionId, r.CallId, "spawn_subagent", fail, cancellationToken).ConfigureAwait(false);
                            toolExecutions++;
                        }
                        PublishTurnEvent(sessionId, "SubAgentsBarrier", new { total = handles.Count, waiting = true });
                        List<SubAgentResult> results;
                        try { results = await coord.BarrierAsync(workspaceId, sessionId, handles, rejected, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                            UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                            return AgentTurnResult.Cancelled(iteration, toolExecutions);
                        }
                        foreach (var res in results)
                        {
                            await AppendToolTextAsync(workspaceId, sessionId, res.CallId, "spawn_subagent", res.ToJson(), cancellationToken).ConfigureAwait(false);
                            toolExecutions++;
                        }
                        PublishTurnEvent(sessionId, "SubAgentsBarrierReleased", new { subs = results.Select(x => new { callId = x.CallId, subId = x.SubSessionId, success = x.Success }).ToArray() });
                    }
                }
            }
        }

         // 理论不可达（有限循环内 iteration>=effectiveMax 即返回；无限循环内必 return），兜底截断
         PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = effectiveMax, toolExecutions, maxIterations = effectiveMax });
         UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
         return AgentTurnResult.Truncated(effectiveMax, toolExecutions);
     }

     private void CollectUsageRow(List<PendingUsageRow> rows, ProviderConfig provider, ModelConfig model, UnifiedTokenUsage? usage, string status, string requestId, string sessionId, string workspaceId)
     {
         // need12-compat：老入口保留，统一转调 UsageRecorder（source=agent）。
         UsageRecorder.Collect(rows, provider, model, usage, status, requestId, sessionId, workspaceId, UsageRecorder.SourceAgent);
     }

    private void FlushUsageRows(List<PendingUsageRow> rows, string sessionId, string workspaceId)
    {
        UsageRecorder.Flush(_usageMetrics, rows, sessionId, workspaceId);
    }

    /// <summary>need13：spawn 回包落盘助手（Tool(callId, text, "spawn_subagent")）。</summary>
    private async Task AppendToolTextAsync(string workspaceId, string sessionId, string callId, string toolName, string text, CancellationToken ct)
    {
        var toolMessage = UnifiedChatMessage.Tool(callId, text, toolName);
        try { await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, toolMessage, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { }
        PublishTurnEvent(sessionId, "ToolExecutionCompleted", new
        {
            iteration = 0,
            callId,
            functionName = toolName,
            success = !text.StartsWith("[SUBAGENT_DISABLED]", StringComparison.Ordinal) && !text.StartsWith("[SUBAGENT_QUOTA_EXCEEDED]", StringComparison.Ordinal) && !text.StartsWith("[SUBAGENT_VALIDATION]", StringComparison.Ordinal) && !text.StartsWith("[SUBAGENT_NESTED_DENIED]", StringComparison.Ordinal),
            exitCode = 0,
            durationMs = 0L,
            argsJson = string.Empty,
            resultText = TruncateForEvent(text, 8000),
            fileBytes = string.Empty,
            updatedAtUtc = DateTime.UtcNow.ToString("o")
        });
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

     public static string HintForTool(string functionName, string? errorCode = null)
     {
         if (!string.IsNullOrEmpty(errorCode))
         {
             switch (errorCode)
             {
                 case "OLD_STRING_NOT_FOUND":
                     return " Hint: open read_file at the suggested line and copy-paste the anchor verbatim, or use edit_lines(offset,limit).";
                 case "ANCHOR_AMBIGUOUS":
                     return " Hint: lengthen the anchor to include the enclosing function/class name, or use edit_lines.";
                 case "NEED_CONFIRM_OVERWRITE":
                     return " Hint: retry write_file with allow_overwrite:true, or use edit_file/edit_lines for partial edits.";
                 case "MISSING_LIMIT":
                     return " Hint: set limit = lines to replace (0 = insert).";
                 case "ANCHOR_TOO_SHORT":
                     return " Hint: lengthen the anchor to at least 8 non-whitespace chars with 2-5 context lines, or use edit_lines(offset,limit).";
                 case "EXPECTED_HASH_MISMATCH":
                     return " Hint: re-read the file with read_file to get the latest hash, then retry.";
                 case "CONTENT_TOO_LARGE":
                     return " Hint: split the change into smaller edit_file/edit_lines calls.";
                 case "OUT_OF_RANGE":
                 case "NO_CHANGE":
                 case "EMPTY_NEW_TEXT":
                 case "REGEX_ZERO_LENGTH":
                     break;
             }
         }

         return functionName switch
         {
             "edit_file" => " Hint: The failure payload lists the nearest line numbers; call read_file on the suggested line, fix the anchor whitespace, or use edit_lines(offset, limit) to edit by line number.",
             "edit_lines" => " Hint: Check offset/limit against read_file's 1-based line numbers, and pass expected_hash when retrying to avoid overwriting concurrent changes.",
             "write_file" => " Hint: Verify the parent directory exists (list_dir) and check path spelling before retrying.",
             "read_file" => " Hint: Use list_dir to confirm the file exists and check relative path spelling.",
             "exec_command" => " Hint: Prefer runner cmd with standard cmd.exe syntax; avoid PowerShell-only syntax.",
             "list_dir" => " Hint: Verify the directory path is relative to the workspace root.",
             "grep_search" => " Hint: Check the search pattern and target directory, then retry with a simpler pattern.",
             _ => " Hint: Check tool arguments and retry."
         };
     }

     /// <summary>need15 P2-3：失败负载直接给出下一步调用草稿（纯文本，不自动执行，≤300 字符）。</summary>
     public static string BuildNextActionHint(ToolResult toolResult, string functionName, string? argumentsJson, string? errorCode = null)
     {
         string code = errorCode ?? toolResult.ErrorCode ?? ToolResult.TryExtractCode(toolResult.Error) ?? string.Empty;
         if (string.IsNullOrEmpty(code))
         {
             return string.Empty;
         }

         try
         {
             if (code is "OLD_STRING_NOT_FOUND" or "ANCHOR_AMBIGUOUS")
             {
                 string payload = toolResult.ErrorPayloadJson ?? toolResult.Error ?? string.Empty;
                 int bestLine = 1;
                 int needleLines = 1;
                 string path = functionName;
                 try
                 {
                     using var doc = System.Text.Json.JsonDocument.Parse(ExtractJsonObject(payload));
                     var root = doc.RootElement;
                     if (root.TryGetProperty("nearest", out var nearest) && nearest.ValueKind == System.Text.Json.JsonValueKind.Array && nearest.GetArrayLength() > 0)
                     {
                         bestLine = nearest[0].TryGetProperty("line", out var l) && l.TryGetInt32(out int lv) ? lv : 1;
                     }

                     if (root.TryGetProperty("old_string_lines", out var nl) && nl.TryGetInt32(out int nlv))
                     {
                         needleLines = Math.Max(1, nlv);
                     }

                     if (root.TryGetProperty("file", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String)
                     {
                         path = f.GetString() ?? path;
                     }
                 }
                 catch
                 {
                 }

                 // 回退：从原始参数里取 file_path
                 if ((path == functionName || string.IsNullOrWhiteSpace(path)) && !string.IsNullOrWhiteSpace(argumentsJson))
                 {
                     try
                     {
                         using var adoc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                         if (adoc.RootElement.TryGetProperty("file_path", out var fp) && fp.ValueKind == System.Text.Json.JsonValueKind.String)
                         {
                             path = fp.GetString() ?? path;
                         }
                     }
                     catch
                     {
                     }
                 }

                 string draft = "{\"file_path\":\"" + path.Replace("\\", "/").Replace("\"", "\\\"") + "\",\"offset\":" + bestLine + ",\"limit\":" + needleLines + ",\"new_text\":\"<paste full replacement here>\"}";
                 string hint = "\nNextAction (copy-paste and adjust): edit_lines(" + draft + ")";
                 return hint.Length > 400 ? hint.Substring(0, 400) : hint;
             }

             if (code is "NEED_CONFIRM_OVERWRITE")
             {
                 return "\nNextAction: retry the SAME write_file call with \"allow_overwrite\":true added.";
             }

             if (code is "MISSING_LIMIT")
             {
                 return "\nNextAction: retry edit_lines with explicit \"limit\" (replace N lines, or 0 to insert).";
             }
         }
         catch
         {
         }

         return string.Empty;
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
         public long ReceivedBytes = 0;
         public long LastPublishedBytes = -1;
         public long LastPublishTick = 0;
     }
}
