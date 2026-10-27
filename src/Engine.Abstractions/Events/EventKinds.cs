namespace HaloAgent.Engine.Abstractions.Events;

/// <summary>事件 kind 常量。与 schema.events 的 key 一一对应。</summary>
public static class EventKinds
{
    /// <summary>turn.started。</summary>
    public const string TurnStarted = "turn.started";
    /// <summary>turn.finished。</summary>
    public const string TurnFinished = "turn.finished";
    /// <summary>text.delta。</summary>
    public const string TextDelta = "text.delta";
    /// <summary>reasoning.delta。</summary>
    public const string ReasoningDelta = "reasoning.delta";
    /// <summary>tool.started。</summary>
    public const string ToolStarted = "tool.started";
    /// <summary>tool.finished。</summary>
    public const string ToolFinished = "tool.finished";
    /// <summary>tool.outputDelta。</summary>
    public const string ToolOutputDelta = "tool.outputDelta";
    /// <summary>tool.silentAlert。</summary>
    public const string ToolSilentAlert = "tool.silentAlert";
    /// <summary>round.started。</summary>
    public const string RoundStarted = "round.started";
    /// <summary>llm.retrying。</summary>
    public const string LlmRetrying = "llm.retrying";
    /// <summary>usage。</summary>
    public const string Usage = "usage";
    /// <summary>notice。</summary>
    public const string Notice = "notice";
    /// <summary>ask.permission（payload = PendingAsk）。</summary>
    public const string AskPermission = "ask.permission";
    /// <summary>ask.rounds（payload = PendingAsk）。</summary>
    public const string AskRounds = "ask.rounds";
    /// <summary>session.info。</summary>
    public const string SessionInfo = "session.info";
    /// <summary>session.historyLoaded。</summary>
    public const string SessionHistoryLoaded = "session.historyLoaded";
    /// <summary>session.contextCleared。</summary>
    public const string SessionContextCleared = "session.contextCleared";
    /// <summary>session.compactionStarted。</summary>
    public const string SessionCompactionStarted = "session.compactionStarted";
    /// <summary>session.compacted。</summary>
    public const string SessionCompacted = "session.compacted";
    /// <summary>user.injected。</summary>
    public const string UserInjected = "user.injected";
    /// <summary>state.personaChanged。</summary>
    public const string StatePersonaChanged = "state.personaChanged";
    /// <summary>state.modeChanged。</summary>
    public const string StateModeChanged = "state.modeChanged";
    /// <summary>state.busyChanged。</summary>
    public const string StateBusyChanged = "state.busyChanged";
    /// <summary>resync.required。</summary>
    public const string ResyncRequired = "resync.required";

    /// <summary>全部事件 kind（供 system.protocol 报告与测试校验）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        TurnStarted, TurnFinished, TextDelta, ReasoningDelta,
        ToolStarted, ToolFinished, ToolOutputDelta, ToolSilentAlert,
        RoundStarted, LlmRetrying, Usage, Notice,
        AskPermission, AskRounds,
        SessionInfo, SessionHistoryLoaded, SessionContextCleared,
        SessionCompactionStarted, SessionCompacted, UserInjected,
        StatePersonaChanged, StateModeChanged, StateBusyChanged, ResyncRequired,
    };
}
