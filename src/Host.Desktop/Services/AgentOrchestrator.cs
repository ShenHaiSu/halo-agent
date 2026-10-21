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

public interface IAgentOrchestrator
{
    int MaxIterations { get; }

    Task<AgentTurnResult> RunAgentTurnAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Stage 06 Agent ReAct 多步循环编排引擎：
/// 用户输入 -&gt; 预算裁剪 -&gt; 双协议流式推理 -&gt; 工具调度执行 -&gt; 结果回写 JSONL -&gt; 下一轮，直至无 ToolCalls。
/// 工具参数瑕疵绝不抛异常中断，转自愈提示回传 LLM；支持 CancellationToken 取消并杀子进程。
/// </summary>
public sealed partial class AgentOrchestrator : IAgentOrchestrator
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
    private readonly IAgentEventSink _sink;

    /// <summary>need13：本实例角色 main|sub（子实例由 Coordinator 构造时传入 sub）。</summary>
    public string AgentRole { get; }

    /// <summary>need13：本实例轮首策略快照（子实例构造时传入；主实例每轮刷新）。</summary>
    public SubPolicySnapshot SubPolicy { get; private set; } = new();

    public AgentOrchestrator(
        IWorkspaceSessionService sessionService,
        ITokenBudgetService budgetService,
        ILlmGatewayClient gatewayClient,
        ToolRegistry? toolRegistry = null,
        StoragePathManager? storageManager = null,
        ProviderConfigService? providerConfigService = null,
        PromptConfigService? promptConfigService = null,
        int maxIterations = 25,
        AgentConfigService? agentConfigService = null,
        UsageMetricsService? usageMetrics = null,
        SubAgentCoordinator? coordinator = null,
        SubPolicySnapshot? subPolicy = null,
        IAgentEventSink? sink = null,
        string agentRole = "main"
    )
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _toolRegistry = toolRegistry ?? new ToolRegistry();
        _storageManager = storageManager;
        _providerConfigService = providerConfigService;
        _promptConfigService = promptConfigService;
        _agentConfigService = agentConfigService;
        _usageMetrics =
            usageMetrics
            ?? (_storageManager != null ? new UsageMetricsService(_storageManager) : null);
        _coordinator = coordinator;
        _sink = sink ?? AgentEventSinkDefaults.Factory();
        AgentRole = string.Equals(agentRole, "sub", StringComparison.OrdinalIgnoreCase)
            ? "sub"
            : "main";
        if (subPolicy != null)
            SubPolicy = subPolicy;
        MaxIterations = maxIterations > 0 ? maxIterations : 25;
        ResolvedMaxIterations = MaxIterations;
    }

    /// <summary>need22-P1 T3：单轮总体超时包装（默认 10min，agent.json/turnTimeoutMs 可配）。
    /// 超时触发时旧轮按 TurnCancelled 已落盘片段保留，并额外发布 TurnError(TIMEOUT) 终轮；用户取消语义不变。</summary>
    public async Task<AgentTurnResult> RunAgentTurnAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default
    )
    {
        int turnTimeoutMs = ResolveTurnTimeoutMs();
        if (turnTimeoutMs <= 0)
            return await RunAgentTurnCoreAsync(
                    workspaceId,
                    sessionId,
                    userText,
                    providerId,
                    modelId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            turnCts.CancelAfter(turnTimeoutMs);
        }
        catch
        { /* 计时器失败则退化为无总体超时 */
        }
        AgentTurnResult result;
        try
        {
            result = await RunAgentTurnCoreAsync(
                    workspaceId,
                    sessionId,
                    userText,
                    providerId,
                    modelId,
                    turnCts.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            string msg = TurnTimeoutMessage(turnTimeoutMs);
            PublishTurnEvent(
                sessionId,
                "TurnError",
                new
                {
                    iteration = 0,
                    errorCode = "TIMEOUT",
                    errorMessage = msg
                }
            );
            return AgentTurnResult.Failed("TIMEOUT", msg);
        }
        if (
            result.WasCancelled
            && turnCts.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
        )
        {
            string msg = TurnTimeoutMessage(turnTimeoutMs);
            PublishTurnEvent(
                sessionId,
                "TurnError",
                new
                {
                    iteration = result.Iterations,
                    errorCode = "TIMEOUT",
                    errorMessage = msg
                }
            );
            return AgentTurnResult.Failed("TIMEOUT", msg, result.Iterations, result.ToolExecutions);
        }
        return result;
    }
}
