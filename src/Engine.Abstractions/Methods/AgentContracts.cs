namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ agent.prompt ============================

/// <summary>agent.prompt 参数。</summary>
public sealed record AgentPromptParams
{
    /// <summary>用户文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>agent.prompt 结果。</summary>
public sealed record AgentPromptResult
{
    /// <summary>是否接受。</summary>
    public bool Accepted { get; init; }
    /// <summary>回合 id。</summary>
    public string TurnId { get; init; } = "";
}

// ============================ agent.queue ============================

/// <summary>agent.queue 参数。</summary>
public sealed record AgentQueueParams
{
    /// <summary>用户文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>agent.queue 结果。</summary>
public sealed record AgentQueueResult
{
    /// <summary>是否已排队。</summary>
    public bool Queued { get; init; }
    /// <summary>队列位置。</summary>
    public int Position { get; init; }
}

// ============================ agent.unqueue ============================

/// <summary>agent.unqueue 参数。</summary>
public sealed record AgentUnqueueParams
{
    /// <summary>要移除的文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>agent.unqueue 结果。</summary>
public sealed record AgentUnqueueResult
{
    /// <summary>是否已移除。</summary>
    public bool Removed { get; init; }
}

// ============================ agent.cancel ============================

/// <summary>agent.cancel 参数。</summary>
public sealed record AgentCancelParams
{
    /// <summary>回合 id（可为空：取消当前回合）。</summary>
    public string? TurnId { get; init; }
}

/// <summary>agent.cancel 结果。</summary>
public sealed record AgentCancelResult
{
    /// <summary>是否已取消。</summary>
    public bool Cancelled { get; init; }
}

// ============================ agent.answer ============================

/// <summary>agent.answer 参数。</summary>
public sealed record AgentAnswerParams
{
    /// <summary>询问 id。</summary>
    public string AskId { get; init; } = "";
    /// <summary>是否允许。</summary>
    public bool Allow { get; init; }
    /// <summary>是否总是允许。</summary>
    public bool Always { get; init; }
}

/// <summary>agent.answer 结果。</summary>
public sealed record AgentAnswerResult
{
    /// <summary>是否接受。</summary>
    public bool Accepted { get; init; }
    /// <summary>拒绝原因：“stale” | “no-pending” | null。</summary>
    public string? Reason { get; init; }
}

// ============================ agent.drain ============================

/// <summary>agent.drain 结果。</summary>
public sealed record AgentDrainResult
{
    /// <summary>排出的文本（无内容时为 null）。</summary>
    public string? Text { get; init; }
}

// ============================ agent.resync ============================
// agent.resync 无参数，结果为 StateSnapshot（见 Types/StateSnapshot.cs）。
