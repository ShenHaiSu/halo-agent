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
    /// <summary>末轮序号（>=1 必需）。</summary>
    public int FinalUsed { get; init; }

    /// <summary>
    /// 本次截断是否由用户主动中断（点“停止”/Esc）造成。
    /// 与 Truncated 的关系：
    ///   - ByUser=true  ⇒ Truncated 必为 true（用户中断一定截断）
    ///   - Truncated=true ⇏ ByUser=true（可能是超长截断 / 轮次超限 / 异常）
    /// 前端据此区分文案：“已由你中断” vs “输出超长已截断” vs “请求失败”。
    /// </summary>
    public bool ByUser { get; init; }
}

/// <summary>text.delta 载荷。</summary>
public sealed record TextDeltaPayload
{
    /// <summary>增量文本。</summary>
    public string Text { get; init; } = "";
    /// <summary>归属轮序号（>=1）。</summary>
    public int Used { get; init; } = 1;
}

/// <summary>reasoning.delta 载荷。</summary>
public sealed record ReasoningDeltaPayload
{
    /// <summary>增量文本。</summary>
    public string Text { get; init; } = "";
    /// <summary>推理种类：full | summary。缺省 full。</summary>
    public string? Kind { get; init; }
    /// <summary>归属轮序号（>=1）。</summary>
    public int Used { get; init; } = 1;
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
    /// <summary>归属轮序号。</summary>
    public int Used { get; init; } = 1;
    /// <summary>截断前参数总长。</summary>
    public int ArgsTotal { get; init; }
    /// <summary>是否截断。</summary>
    public bool Truncated { get; init; }
}

/// <summary>tool.finished 载荷。</summary>
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
     /// <summary>归属轮序号。</summary>
     public int Used { get; init; } = 1;
     /// <summary>截断前输出总长。</summary>
     public int OutputTotal { get; init; }
     /// <summary>是否截断。</summary>
     public bool Truncated { get; init; }
     /// <summary>true 表示未实际执行（如截断）。缺省 false。</summary>
     public bool? Skipped { get; init; }
     /// <summary>未执行原因：truncated | blocked。缺省 null。</summary>
     public string? SkipReason { get; init; }
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

/// <summary>round.ended 载荷。</summary>
public sealed record RoundEndedPayload
{
    /// <summary>轮序号。</summary>
    public int Used { get; init; }
    /// <summary>是否有工具调用（true=中途轮，false=末轮）。</summary>
    public bool HasTools { get; init; }
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
     /// <summary>本轮 cached token 数（默认 0，老前端忽略）。</summary>
     public long Cached { get; init; }
     /// <summary>递增后的本会话 LLM 调用总值（默认 0，老前端忽略）。</summary>
     public long LlmCalls { get; init; }
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
    /// <summary>回合视图。</summary>
    public IReadOnlyList<TurnView> Turns { get; init; } = Array.Empty<TurnView>();
}

 /// <summary>session.compacted 载荷。</summary>
 public sealed record SessionCompactedPayload
 {
     /// <summary>移除条数。</summary>
     public int Removed { get; init; }
     /// <summary>摘要。</summary>
     public string Summary { get; init; } = "";
     /// <summary>保留的回合视图（deprecated，保留兼容；新端优先用 Turns）。</summary>
     public IReadOnlyList<TurnView> Kept { get; init; } = Array.Empty<TurnView>();
     /// <summary>压缩后全量回合视图（含摘要轮），前端以此替换 turns。</summary>
     public IReadOnlyList<TurnView> Turns { get; init; } = Array.Empty<TurnView>();
     /// <summary>压缩前消息数（含 system）。</summary>
     public int TotalBefore { get; init; }
     /// <summary>压缩后消息数（含 system）。</summary>
     public int TotalAfter { get; init; }
     /// <summary>本会话内压缩序号。</summary>
     public int Seq { get; init; }
     /// <summary>本次折叠的回合数。</summary>
     public int RemovedTurns { get; init; }
     /// <summary>本次保留的回合数。</summary>
     public int KeptTurns { get; init; }
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

/// <summary>tool.result 参数（）。</summary>
public sealed record ToolResultParams
{
    public string ToolCallId { get; init; } = "";
    public int Offset { get; init; }
    public int Limit { get; init; } = 8000;
}

/// <summary>tool.result 结果。</summary>
public sealed record ToolResultResult
{
    public string ToolCallId { get; init; } = "";
    public string ArgsSlice { get; init; } = "";
    public string OutputSlice { get; init; } = "";
    public int ArgsTotal { get; init; }
    public int OutputTotal { get; init; }
    public bool Found { get; init; }
    public string? Reason { get; init; }
}
