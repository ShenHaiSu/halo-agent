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
     /// <summary>need22-P1 T3：单轮总体超时包装（默认 10min，agent.json/turnTimeoutMs 可配）。
     /// 超时触发时旧轮按 TurnCancelled 已落盘片段保留，并额外发布 TurnError(TIMEOUT) 终轮；用户取消语义不变。</summary>
     public async Task<AgentTurnResult> RunAgentTurnAsync(string workspaceId, string sessionId, string userText, string? providerId = null, string? modelId = null, CancellationToken cancellationToken = default)
     {
         int turnTimeoutMs = ResolveTurnTimeoutMs();
         if (turnTimeoutMs <= 0)
             return await RunAgentTurnCoreAsync(workspaceId, sessionId, userText, providerId, modelId, cancellationToken).ConfigureAwait(false);
         using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
         try { turnCts.CancelAfter(turnTimeoutMs); } catch { /* 计时器失败则退化为无总体超时 */ }
         AgentTurnResult result;
         try
         {
             result = await RunAgentTurnCoreAsync(workspaceId, sessionId, userText, providerId, modelId, turnCts.Token).ConfigureAwait(false);
         }
         catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
         {
             string msg = TurnTimeoutMessage(turnTimeoutMs);
             PublishTurnEvent(sessionId, "TurnError", new { iteration = 0, errorCode = "TIMEOUT", errorMessage = msg });
             return AgentTurnResult.Failed("TIMEOUT", msg);
         }
         if (result.WasCancelled && turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
         {
             string msg = TurnTimeoutMessage(turnTimeoutMs);
             PublishTurnEvent(sessionId, "TurnError", new { iteration = result.Iterations, errorCode = "TIMEOUT", errorMessage = msg });
             return AgentTurnResult.Failed("TIMEOUT", msg, result.Iterations, result.ToolExecutions);
         }
         return result;
     }

     private int ResolveTurnTimeoutMs()
     {
         try
         {
             if (_agentConfigService != null)
             {
                 int v = _agentConfigService.GetInternal().TurnTimeoutMs;
                 if (v <= 0) return AgentDefaults.TurnTimeoutMs;
                 return Math.Clamp(v, AgentDefaults.TurnTimeoutMinMs, AgentDefaults.HardTimeoutCeilMs);
             }
         }
         catch { /* 配置读取失败回默认不断会话 */ }
         return AgentDefaults.TurnTimeoutMs;
     }

     private static string TurnTimeoutMessage(int ms) =>
         $"本轮执行超过总体超时（{ms / 1000}s）已终止，已落盘片段予以保留；长任务请拆小步后继续，或调大 config/agent.json 的 turnTimeoutMs（默认 600000 即 10min）。";

    private async Task<AgentTurnResult> RunAgentTurnCoreAsync(string workspaceId, string sessionId, string userText, string? providerId = null, string? modelId = null, CancellationToken cancellationToken = default)
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
         // need22-P1 T7：本轮子耗 rollup（Barrier 结果累加，TurnCompleted 回填真实子耗）。
         long subBilledIn = 0;
         long subBilledOut = 0;
         int subTotalCount = 0;
         // need22-P1 T3：连续同错计数（空工具名/工具失败同码连续 3 次直接终轮）。
         string? lastToolErrorCode = null;
         int consecutiveToolErrors = 0;
         bool NoteToolError(string code)
         {
             if (string.Equals(lastToolErrorCode, code, StringComparison.Ordinal)) consecutiveToolErrors++;
             else { lastToolErrorCode = code; consecutiveToolErrors = 1; }
             return consecutiveToolErrors >= 3;
         }
         void NoteToolSuccess() { lastToolErrorCode = null; consecutiveToolErrors = 0; }
         // need12：轮内用量行收集（每次 HTTP 请求一行，轮末单事务批量写库）。
         var usageRows = new List<PendingUsageRow>();
        // need08：轮首快照（追加 user 之后、循环之前，只读一次；保存不中断本轮，下一轮生效）
        // need17-01/联调：工具激活配置 + strict 模式同样轮首快照（本轮冻结；description 声明与 Dispatch 执行同源）。
        int effectiveMax = MaxIterations;
        int cfgExecDefaultMs = AgentDefaults.DefaultTimeoutMs;
        int cfgExecMaxMs = AgentDefaults.MaxTimeoutMs;
        System.Collections.Generic.List<string>? cfgAllowedCommands = null;
        string? cfgToolsPreset = null;
        System.Collections.Generic.List<string>? cfgToolsAllow = null;
        System.Collections.Generic.List<string>? cfgToolsDeny = null;
        bool cfgClosedWorld = true;
        try
        {
            if (_agentConfigService != null)
            {
                var cfg = _agentConfigService.GetInternal();
                cfgExecDefaultMs = cfg.ExecDefaultTimeoutMs;
                cfgExecMaxMs = cfg.ExecMaxTimeoutMs;
                cfgAllowedCommands = cfg.AllowedCommands;
                cfgToolsPreset = cfg.ToolsPreset;
                cfgToolsAllow = cfg.ToolsAllowlist;
                cfgToolsDeny = cfg.ToolsDenylist;
                cfgClosedWorld = cfg.StrictToolsMode != Host.Desktop.Core.Models.StrictToolsMode.Off;
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
        // need17-01/联调：本轮激活集轮首计算一次、本轮冻结（声明与执行同源；失败回退全量 Direct）。
        System.Collections.Generic.HashSet<string> turnActiveSet;
        try
        {
            var toolsCfgDoc = new Host.Desktop.Core.Models.AgentConfigDocument { ToolsPreset = cfgToolsPreset, ToolsAllowlist = cfgToolsAllow, ToolsDenylist = cfgToolsDeny };
            var resolved = Host.Desktop.Tools.ToolPreset.ResolveActiveSet(_toolRegistry.ToolNames, toolsCfgDoc);
            turnActiveSet = new System.Collections.Generic.HashSet<string>(resolved, System.StringComparer.OrdinalIgnoreCase);
        }
        catch { turnActiveSet = new System.Collections.Generic.HashSet<string>(_toolRegistry.ToolNames, System.StringComparer.OrdinalIgnoreCase); }
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
                 // FIX(tool-400)：同轮内上次迭代可能留下孤儿（取消/截断死在 assistant→tool 之间），本轮发包前内存愈合，不写盘。
                 try
                 {
                     var (healed, _, _) = WorkspaceSessionService.RebuildHealedHistory(history);
                     history = healed;
                 }
                 catch { /* 愈合失败则用原历史，适配器发包前仍有守卫 */ }
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
            string sessionHash = !string.IsNullOrWhiteSpace(session!.SessionHash)
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

             // need17-01/联调：声明走本轮冻结集（与执行同源；用户 preset 生效，不再是写死的 Coding）。
             var toolDefs = _toolRegistry.GetActiveDefinitions(turnActiveSet)
                 .Select(d => UnifiedToolDefinition.Create(d.Name, d.Description, d.ParametersJson))
                 .ToList();
             // need11-01：读盘→dataURL 本轮内存缓存（重启/多会话安全，无全局锁；null 也缓存避免重复读丢失文件）
             // need22-P1 T6：本轮图片字节预算 8MB（缩略图走 GetImageThumbnail 懒取；超预算原图降级跳过并 Warn，防多图 OOM）。
             var imgCache = new Dictionary<string, string?>(StringComparer.Ordinal);
             long imgUsedBytes = 0;
             const long ImgBudgetBytes = 8L * 1024 * 1024;
             Func<ChatImageRef, string?> imgResolver = img =>
             {
                 if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
                 string key = img.ImageId.Trim();
                 if (imgCache.TryGetValue(key, out var hit)) return hit;
                 string? url = ImageAttachmentStore.ResolveDataUrl(dataRoot, workspaceId, sessionId, img);
                 if (url != null && imgUsedBytes + url.Length > ImgBudgetBytes)
                 {
                     Infrastructure.Logging.HaloLogger.App.Warn($"[Image] 本轮图片字节超预算（{ImgBudgetBytes}），已跳过 {key}（前端可经 GetImageThumbnail 懒取）");
                     imgCache[key] = null;
                     return null;
                 }
                 if (url != null) imgUsedBytes += url.Length;
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
            bool streamTruncated = false; // NEED17-06 §6.4
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
             var emptyNameCids = new List<string>();
             foreach (string cid in toolOrder)
             {
                 var p = pendingTools[cid];
                 if (string.IsNullOrWhiteSpace(p.FunctionName))
                 {
                     emptyNameCids.Add(cid);
                     continue;
                 }
                 completedCalls.Add(new UnifiedToolCall(cid, p.ItemId, p.FunctionName, p.ArgsBuilder.ToString()));
             }
             // need22-P1 T3：空 FunctionName 收集（回包落在 assistant 之后，保证 tool_calls→tool 先后序；见 AssistantMessagePersisted 后处理）。
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
             // need22-P1 T3：空 FunctionName 回包（assistant 之后，保证 tool_calls→tool 先后序）+ EMPTY_TOOL_NAME 连续失败终轮。
             if (emptyNameCids.Count > 0)
             {
                 foreach (string ecid in emptyNameCids)
                 {
                     string emptyText = "[EMPTY_TOOL_NAME] 模型返回了缺少工具名的调用，已忽略。请用完整 functionName 重发该调用。";
                     try { await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, UnifiedChatMessage.Tool(ecid, emptyText, "unknown"), cancellationToken).ConfigureAwait(false); }
                     catch (OperationCanceledException)
                     {
                         PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
                         UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                         return AgentTurnResult.Cancelled(iteration, toolExecutions);
                     }
                     catch { /* 落盘失败不掩盖错误码语义 */ }
                     PublishTurnEvent(sessionId, "ToolExecutionCompleted", new { iteration, callId = ecid, functionName = "", success = false, exitCode = -1, durationMs = 0L, argsJson = "", resultText = emptyText, updatedAtUtc = DateTime.UtcNow.ToString("o") });
                     toolExecutions++;
                 }
                 // need22-P1 T3/无限模式兼容：无限轮次（effectiveMax<=0）下取消即停（S08 契约），不提前截断；
                 // 有限轮次下同码连续 3 次提前截断暂停（Success=true，可“继续”；与 maxIterations 截断同契约）。
                 if (effectiveMax > 0 && NoteToolError("EMPTY_TOOL_NAME"))
                 {
                     // need22-P1 T3：同码连续 3 次提前截断暂停（Success=true，可“继续”；与 maxIterations 截断同契约）。
                     string msgEmpty3 = "同一工具错误（EMPTY_TOOL_NAME：空工具名）连续出现 3 次，已暂停自动执行。请检查模型输出后发送“继续”以恢复任务。";
                     try
                     {
                         await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, UnifiedChatMessage.Assistant(msgEmpty3), CancellationToken.None).ConfigureAwait(false);
                     }
                     catch { /* ignore */ }
                     PublishTurnEvent(sessionId, "TurnTruncated", new { iterations = iteration, toolExecutions, maxIterations = effectiveMax, reason = "repeated-tool-error", errorCode = "EMPTY_TOOL_NAME" });
                     UsageRecorder.Flush(_usageMetrics, usageRows, sessionId, workspaceId);
                     return AgentTurnResult.Truncated(iteration, toolExecutions);
                 }
             }

             // (6) 无 ToolCalls（含空名调用已单独处理）则 TurnCompleted 结束
              if (completedCalls.Count == 0 && emptyNameCids.Count == 0)
              {
                  // need13/T7：主 TurnCompleted 回填真实子耗 rollup（Barrier 累加，非 0；供顶栏“本轮含子耗”）
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
                      subAgents = new { count = subTotalCount, billedPrompt = subBilledIn, billedCompletion = subBilledOut },
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

            // NEED17-06 §6.4：截断回复保护（只加保护，不加并行；该轮不计 toolExecutions）
            if (streamTruncated && completedCalls.Count > 0)
            {
                foreach (var call in completedCalls)
                {
                    string discarded = $"[Tool Discarded - {call.FunctionName}]: 调用未执行：回复因输出长度被截断，参数可能不完整。请用完整参数重发（可缩小单次改动）。";
                    await AppendToolTextAsync(workspaceId, sessionId, call.CallId, call.FunctionName, discarded, cancellationToken).ConfigureAwait(false);
                    PublishTurnEvent(sessionId, "ToolExecutionCompleted", new { iteration, callId = call.CallId, functionName = call.FunctionName, success = false, exitCode = -1, durationMs = 0L, argsJson = call.ArgumentsJson, resultText = TruncateForEvent(discarded, 8000), updatedAtUtc = DateTime.UtcNow.ToString("o") });
                }
                continue; // 进入下一 iteration（模型见丢弃通知后重发完整参数）
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
                        AllowedCommandsOverride = cfgAllowedCommands,
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
                    // need17-01/02/联调：执行走本轮冻结集 + strict 闭合开关（与声明同源；Off 回滚时未知字段降级 warning）。
                    toolResult = await _toolRegistry.DispatchAsync(dto, toolContext, turnActiveSet, cancellationToken, cfgClosedWorld).ConfigureAwait(false);
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
                  // need22-P1 T6：大 toolOutput（>64KB）先落盘 fileRef{path,size}，content 只留前 4K 预览（messages.jsonl 信封格式不变）。
                  toolText = SpillLargeToolOutput(toolText, dataRoot, workspaceId, sessionId, call.CallId);

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
                 // need22-P1 T3：工具失败连续同码 3 次提前截断暂停（防空转多轮耗费；Success=true，可“继续”）。
                 if (toolResult.Success) NoteToolSuccess();
                 else
                 {
                     string failCode = string.IsNullOrEmpty(errorCode) ? "TOOL_FAILED" : errorCode;
                     if (effectiveMax > 0 && NoteToolError(failCode))
                     {
                         string msgFail3 = $"同一工具错误（{failCode}）连续出现 3 次，已暂停自动执行。请检查参数后发送“继续”以恢复任务。";
                         try
                         {
                             await _sessionService.AppendSessionMessageAsync(workspaceId, sessionId, UnifiedChatMessage.Assistant(msgFail3), CancellationToken.None).ConfigureAwait(false);
                         }
                         catch { /* ignore */ }
                         PublishTurnEvent(sessionId, "TurnTruncated", new { iteration, toolExecutions, maxIterations = effectiveMax, reason = "repeated-tool-error", errorCode = failCode });
                         FlushUsageRows(usageRows, sessionId, workspaceId);
                         return AgentTurnResult.Truncated(iteration, toolExecutions);
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
                         // need22-P1 T7：Barrier 全部子 usage 累加（本轮 TurnCompleted 回填真实子耗）。
                         subTotalCount += results.Count;
                         foreach (var res in results) { subBilledIn += res.InputTokens; subBilledOut += res.OutputTokens; }
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
                // NEED17-06 §6.3 + 02 码表：新增结构码 Hint（一句英文，拼在 tool 消息尾）。
                case "MISSING_REQUIRED_PARAM":
                    return " Hint: re-issue the same call with the missing field added; do not change other fields.";
                case "TYPE_MISMATCH":
                case "ENUM_VIOLATION":
                case "CONSTRAINT_VIOLATION":
                case "UNKNOWN_ARGUMENT":
                    return " Hint: fix the named field per schema (type/enum/limit) and retry; see the received-args echo.";
                case "TOOL_NOT_ENABLED":
                    return " Hint: the tool is not in this turn's preset; use an active tool or ask for Full preset.";
                case "INVALID_JSON_ARGUMENTS":
                    return " Hint: resend with complete JSON (no truncation); keep strings escaped.";
                // NEED17-07 §7.1：全仓库唯一中文 Hint（逐字）。07 快照/Gate 落子后保持文案不变。
                case "PYTHON_NOT_AVAILABLE":
                    return " Hint: 本机无 Python，请用 runner:'cmd' 继续；需要 Python 请按上方指引安装后重启 HaloAgent。";
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

            // NEED17-06 §6.3：MISSING_REQUIRED_PARAM 最小补全草稿（{缺失字段:"<fill>"} ≤300 字符，纯文本不执行）。
            if (code is "MISSING_REQUIRED_PARAM")
            {
                string draft = BuildMissingFieldDraft(toolResult, argumentsJson);
                if (!string.IsNullOrEmpty(draft))
                    return "\nNextAction (fill and resend): " + draft;
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

    /// <summary>need17-06 §6.3：缺失字段补全草稿（{缺失字段:"<fill>"}，≤300 字符，纯文本不执行）。字段来源：payload missing[]/field → Error 引号 → 通用占位。</summary>
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
                if (root.TryGetProperty("missing", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var e in m.EnumerateArray())
                        if (e.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                            fields.Add(e.GetString()!);
                }
                else if (root.TryGetProperty("field", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(f.GetString()))
                {
                    fields.Add(f.GetString()!);
                }
            }
        }
        catch { /* 解析失败则走回退 */ }
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
                    if (cand.Length > 0 && cand.Length <= 64) fields.Add(cand);
                }
            }
            catch { /* ignore */ }
        }
        if (fields.Count == 0) return "{\"missing-field\":\"<fill>\"}";
        string draft = "{" + string.Join(",", fields.Take(5).Select(n => "\"" + n.Replace("\\", "/").Replace("\"", "\\\"") + "\":\"<fill>\"")) + "}";
        return draft.Length > 300 ? draft.Substring(0, 300) : draft;
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
     /// 落盘失败回退为 8K 截断；messages.jsonl 信封格式不变（仍是 tool 文本消息）。</summary>
     internal const long ToolOutputSpillThresholdBytes = 64L * 1024;
     internal const int ToolOutputPreviewChars = 4096;
     internal static string SpillLargeToolOutput(string toolText, string? dataRoot, string workspaceId, string sessionId, string callId)
     {
         try
         {
             if (string.IsNullOrEmpty(toolText)) return toolText;
             long approxBytes;
             try { approxBytes = System.Text.Encoding.UTF8.GetByteCount(toolText); }
             catch { approxBytes = toolText.Length; }
             if (approxBytes <= ToolOutputSpillThresholdBytes) return toolText;
             string Safe(string s, int max)
             {
                 if (string.IsNullOrWhiteSpace(s)) return "x";
                 var chars = s.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
                 string r = new string(chars);
                 return r.Length > max ? r.Substring(0, max) : r;
             }
             string baseDir = string.IsNullOrWhiteSpace(dataRoot) ? System.IO.Path.GetTempPath() : dataRoot;
             string dir = System.IO.Path.Combine(baseDir, "tool-outputs", Safe(workspaceId, 32) + "_" + Safe(sessionId, 32));
             System.IO.Directory.CreateDirectory(dir);
             string fileName = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Safe(callId, 32) + ".txt";
             string abs = System.IO.Path.Combine(dir, fileName);
             System.IO.File.WriteAllText(abs, toolText, System.Text.Encoding.UTF8);
             long size = 0;
             try { size = new System.IO.FileInfo(abs).Length; } catch { size = approxBytes; }
             string preview = toolText.Length > ToolOutputPreviewChars ? toolText.Substring(0, ToolOutputPreviewChars) : toolText;
             return preview + $"\n...（工具输出约 {approxBytes} 字节，超 64KB 已落盘，仅保留前 4K 预览；全文见 fileRef）\n[fileRef path=\"{abs}\" size={size}]";
         }
         catch
         {
             return toolText.Length > 8000 ? toolText.Substring(0, 8000) + "\n...（输出过长已截断）" : toolText;
         }
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
