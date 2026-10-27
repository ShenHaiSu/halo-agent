namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>
/// 一个待用户回答的询问。
/// 关键约束（引擎侧保证）：同一时刻最多只有一个；askId 单调递增，
/// 过期回答被拒绝；warning != null 时忽略前端传来的 always=true。
/// </summary>
public sealed record PendingAsk
{
    /// <summary>询问 id。</summary>
    public string AskId { get; init; } = "";
    /// <summary>“command” | “write” | “rounds”。</summary>
    public string Kind { get; init; } = "";
    /// <summary>标题。</summary>
    public string Title { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
    /// <summary>高危/无法判定的说明（前端红色渲染，且不给“总是允许”）。</summary>
    public string? Warning { get; init; }
    /// <summary>是否允许“总是允许”。</summary>
    public bool AllowAlways { get; init; }
    /// <summary>kind=rounds 时有效：已用轮数。</summary>
    public int? Used { get; init; }

    /// <summary>创建权限类询问（command/write）。</summary>
    public static PendingAsk Permission(
        string askId, string kind, string title, string body,
        string? warning, bool allowAlways) => new()
    {
        AskId = askId,
        Kind = kind,
        Title = title,
        Body = body,
        Warning = warning,
        AllowAlways = allowAlways,
    };

    /// <summary>创建轮次预算类询问（rounds）。</summary>
    public static PendingAsk Rounds(string askId, int used) => new()
    {
        AskId = askId,
        Kind = "rounds",
        Title = "轮次预算用尽",
        Body = $"已用 {used} 轮，任务可能还没完。继续会再放一批。",
        AllowAlways = false,
        Used = used,
    };
}
