using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// need21-B03: 单轮流水线上下文。承载 PreTurn  setup 与循环携带状态，
/// 替代原 RunAgentTurnCoreAsync 内 ~40 个跨阶段 locals（含 NoteToolError/Success 本地函数）。
/// 阶段类只经此对象交换数据，不再共享方法 locals。
/// </summary>
public sealed class TurnContext
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string UserText { get; init; } = string.Empty;
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public WorkspaceInfo? Workspace { get; set; }
    public SessionMetadata? Session { get; set; }
    public UnifiedChatMessage? UserMessage { get; set; }
    public ProviderConfig Provider { get; set; } = null!;
    public ModelConfig Model { get; set; } = null!;
    public string? SkillIndexBlock { get; set; }
    public string? MemoryPinnedBlock { get; set; }
    public int ResidentStableTokens { get; set; }
    public bool SnapVision { get; set; }
    public int SnapMaxImages { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public string SubPolicyBlock { get; set; } = string.Empty;
    public string? GoalBlock { get; set; }
    public string DataRoot { get; set; } = string.Empty;
    public int EffectiveMax { get; set; }
    public int CfgExecDefaultMs { get; set; }
    public int CfgExecMaxMs { get; set; }
    public List<string>? CfgAllowedCommands { get; set; }
    public string? CfgToolsPreset { get; set; }
    public List<string>? CfgToolsAllow { get; set; }
    public List<string>? CfgToolsDeny { get; set; }
    public bool CfgClosedWorld { get; set; } = true;
    public HashSet<string> TurnActiveSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ToolExecutions { get; set; }
    public long TurnBilledIn { get; set; }
    public long TurnBilledOut { get; set; }
    public long SubBilledIn { get; set; }
    public long SubBilledOut { get; set; }
    public int SubTotalCount { get; set; }
    public List<PendingUsageRow> UsageRows { get; } = new();

    private string? _lastToolErrorCode;
    private int _consecutiveToolErrors;

    public int ConsecutiveToolErrors => _consecutiveToolErrors;

    public bool NoteToolError(string code)
    {
        if (string.Equals(_lastToolErrorCode, code, StringComparison.Ordinal))
            _consecutiveToolErrors++;
        else
        {
            _lastToolErrorCode = code;
            _consecutiveToolErrors = 1;
        }
        return _consecutiveToolErrors >= 3;
    }

    public void NoteToolSuccess()
    {
        _lastToolErrorCode = null;
        _consecutiveToolErrors = 0;
    }

    public List<UnifiedChatMessage> History { get; set; } = new();
    public List<UnifiedChatMessage> Budgeted { get; set; } = new();
    public List<UnifiedChatMessage> RequestMessages { get; set; } = new();
    public string RequestId { get; set; } = string.Empty;
    public string LlmText { get; set; } = string.Empty;
    public string? LlmReasoning { get; set; }
    public List<UnifiedToolCall> CompletedCalls { get; set; } = new();
    public List<string> EmptyNameCids { get; set; } = new();
    public UnifiedTokenUsage? CurrentUsage { get; set; }
    public bool StreamTruncated { get; set; }
}

/// <summary>轮首准备结果：Terminal 非空即直接返回，不进循环。</summary>
public sealed class TurnPrepared
{
    public AgentTurnResult? Terminal { get; init; }
    public TurnContext? Ctx { get; init; }
}

/// <summary>阶段终端判定：Terminal 非空即直接返回该结果。</summary>
public sealed class PhaseOutcome
{
    public AgentTurnResult? Terminal { get; init; }

    public static PhaseOutcome Continue() => new();

    public static PhaseOutcome End(AgentTurnResult terminal) => new() { Terminal = terminal };
}

/// <summary>need21-B03: 请求批次（BuildRequest 输出：网关请求 + 请求 id；消息列表同步入 ctx.RequestMessages）。</summary>
public sealed class RequestBatch
{
    public UnifiedLlmRequest Request { get; init; } = null!;
}

/// <summary>need21-B03: 流式状态（BuildRequest→Pump→Post 间传递的可变累加器）。</summary>
public sealed class LlmStreamState
{
    public UnifiedLlmRequest Request { get; init; } = null!;
    public System.Text.StringBuilder TextAcc { get; } = new();
    public System.Text.StringBuilder ReasoningAcc { get; } = new();
    public Dictionary<
        string,
        Host.Desktop.Services.AgentOrchestrator.PendingToolCall
    > PendingTools { get; set; } = new();
    public List<string> ToolOrder { get; set; } = new();
    public UnifiedTokenUsage? Usage { get; set; }
    public bool StreamCancelled { get; set; }
    public bool StreamTruncated { get; set; }
    public string? StreamErrorCode { get; set; }
    public string? StreamErrorMessage { get; set; }
    public long TtftMark { get; set; }
    public bool TtftSent { get; set; }
}
