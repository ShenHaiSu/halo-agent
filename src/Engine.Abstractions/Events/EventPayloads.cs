using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Events;

/// <summary>turn.started 载荷。</summary>
public sealed record TurnStartedPayload
{
    /// <summary>回合 id。</summary>
    public string TurnId { get; init; } = "";
}

/// <summary>turn.finished 载荷。</summary>
public sealed record TurnFinishedPayload
{
    /// <summary>回合 id。</summary>
    public string TurnId { get; init; } = "";
    /// <summary>最终文本。</summary>
    public string Text { get; init; } = "";
    /// <summary>是否被截断。</summary>
    public bool Truncated { get; init; }
    /// <summary>结束原因（可为空）。</summary>
    public string? Reason { get; init; }
    /// <summary>诊断用 response.id，不做链逻辑依赖。</summary>
    public string? ResponseId { get; init; }
}

/// <summary>text.delta 载荷。</summary>
public sealed record TextDeltaPayload
{
    /// <summary>增量文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>reasoning.delta 载荷。</summary>
public sealed record ReasoningDeltaPayload
{
    /// <summary>增量文本。</summary>
    public string Text { get; init; } = "";
    /// <summary>推理种类：full | summary。缺省 full。</summary>
    public string? Kind { get; init; }
}

/// <summary>tool.started 载荷。</summary>
public sealed record ToolStartedPayload
{
    /// <summary>工具调用 id。</summary>
    public string ToolCallId { get; init; } = "";
    /// <summary>工具名。</summary>
    public string Name { get; init; } = "";
    /// <summary>调用参数（JSON 字符串）。</summary>
    public string Args { get; init; } = "";
    /// <summary>空闲毫秒数（可为空）。</summary>
    public long? IdleMs { get; init; }
    /// <summary>工具种类：function | unknown。缺省 function。</summary>
    public string? Kind { get; init; }
    /// <summary>原生输出类型。</summary>
    public string? RawType { get; init; }
}

/// <summary>tool.finished 载荷。</summary>
public sealed record ToolFinishedPayload
{
    /// <summary>工具调用 id。</summary>
    public string ToolCallId { get; init; } = "";
    /// <summary>工具名。</summary>
    public string Name { get; init; } = "";
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>输出。</summary>
    public string Output { get; init; } = "";
}

/// <summary>tool.outputDelta 载荷。</summary>
public sealed record ToolOutputDeltaPayload
{
    /// <summary>工具调用 id。</summary>
    public string ToolCallId { get; init; } = "";
    /// <summary>增量输出。</summary>
    public string Delta { get; init; } = "";
}

/// <summary>tool.silentAlert 载荷。</summary>
public sealed record ToolSilentAlertPayload
{
    /// <summary>工具调用 id。</summary>
    public string ToolCallId { get; init; } = "";
    /// <summary>级别：0~2。</summary>
    public int Level { get; init; }
}

/// <summary>round.started 载荷。</summary>
public sealed record RoundStartedPayload
{
    /// <summary>已用轮数。</summary>
    public int Used { get; init; }
    /// <summary>轮数上限。</summary>
    public int Limit { get; init; }
}

/// <summary>llm.retrying 载荷。</summary>
public sealed record LlmRetryingPayload
{
    /// <summary>当前尝试次数。</summary>
    public int Attempt { get; init; }
    /// <summary>最大尝试次数。</summary>
    public int Max { get; init; }
    /// <summary>原因。</summary>
    public string Reason { get; init; } = "";
}

/// <summary>usage 载荷。</summary>
public sealed record UsagePayload
{
    /// <summary>prompt token 数。</summary>
    public long Prompt { get; init; }
    /// <summary>completion token 数。</summary>
    public long Completion { get; init; }
    /// <summary>本批是否因背压丢弃过 delta。</summary>
    public bool? Dropped { get; init; }
}

/// <summary>notice 载荷。</summary>
public sealed record NoticePayload
{
    /// <summary>文本。</summary>
    public string Text { get; init; } = "";
    /// <summary>“info” | “warn” | “error”。</summary>
    public string Level { get; init; } = "info";
}

/// <summary>session.info 载荷。</summary>
public sealed record SessionInfoPayload
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>工作目录。</summary>
    public string Cwd { get; init; } = "";
    /// <summary>历史文件路径（可为空）。</summary>
    public string? HistoryPath { get; init; }
}

/// <summary>session.historyLoaded 载荷。</summary>
public sealed record SessionHistoryLoadedPayload
{
    /// <summary>消息条数。</summary>
    public int Count { get; init; }
    /// <summary>消息。</summary>
    public IReadOnlyList<MessageView> Messages { get; init; } = Array.Empty<MessageView>();
}

/// <summary>session.compacted 载荷。</summary>
public sealed record SessionCompactedPayload
{
    /// <summary>移除条数。</summary>
    public int Removed { get; init; }
    /// <summary>摘要。</summary>
    public string Summary { get; init; } = "";
    /// <summary>保留的消息。</summary>
    public IReadOnlyList<MessageView> Kept { get; init; } = Array.Empty<MessageView>();
}

/// <summary>user.injected 载荷。</summary>
public sealed record UserInjectedPayload
{
    /// <summary>注入的文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>state.personaChanged 载荷。</summary>
public sealed record PersonaChangedPayload
{
    /// <summary>新人格名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>state.modeChanged 载荷。</summary>
public sealed record ModeChangedPayload
{
    /// <summary>新权限模式。</summary>
    public PermissionMode Mode { get; init; }
    /// <summary>界面标签。</summary>
    public string Label { get; init; } = "";
}

/// <summary>state.busyChanged 载荷。</summary>
public sealed record BusyChangedPayload
{
    /// <summary>是否忙碌。</summary>
    public bool Busy { get; init; }
    /// <summary>队列长度。</summary>
    public int QueueLength { get; init; }
}

/// <summary>resync.required 载荷。</summary>
public sealed record ResyncRequiredPayload
{
    /// <summary>原因。</summary>
    public string Reason { get; init; } = "";
}
