namespace HaloAgent.Engine.Core.Permissions;

public enum PermDecisionKind { Allow, Deny, NeedAsk }

public sealed record PermDecision
{
    public PermDecisionKind Kind { get; init; }
    /// <summary>Deny 时的原因</summary>
    public string? Reason { get; init; }
    /// <summary>NeedAsk 时的标题</summary>
    public string? Title { get; init; }
    /// <summary>NeedAsk 时的正文</summary>
    public string? Body { get; init; }
    /// <summary>NeedAsk 时的红色警告（高危/无法判定）</summary>
    public string? Warning { get; init; }
    /// <summary>是否允许"本会话都允许"（有 warning 时恒为 false）</summary>
    public bool AllowAlways { get; init; }

    public static PermDecision Allow => new() { Kind = PermDecisionKind.Allow };

    public static PermDecision Deny(string reason) =>
        new() { Kind = PermDecisionKind.Deny, Reason = reason };

    public static PermDecision NeedAsk(
        string title, string body, string? warning, bool allowAlways) => new()
    {
        Kind = PermDecisionKind.NeedAsk,
        Title = title,
        Body = body,
        Warning = warning,
        // 硬约束：有 warning 就不许 always
        AllowAlways = allowAlways && warning is null,
    };
}
