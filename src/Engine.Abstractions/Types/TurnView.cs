namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>P15：回合视图。用户消息 + 同回合排队插入 + 中途产出 + 末轮正文。</summary>
public sealed record TurnView
{
    /// <summary>回合 id。</summary>
    public string TurnId { get; init; } = "";
    /// <summary>回合首条 user 消息。</summary>
    public MessageView User { get; init; } = new();
    /// <summary>同回合排队插入的 user 消息。</summary>
    public IReadOnlyList<MessageView> Injected { get; init; } = Array.Empty<MessageView>();
    /// <summary>中途产出（含占位 tool，Ord 排好）。</summary>
    public IReadOnlyList<MessageView> Process { get; init; } = Array.Empty<MessageView>();
    /// <summary>末轮正文（必有 assistant 占位，无 null）。</summary>
    public MessageView Final { get; init; } = new();
}
