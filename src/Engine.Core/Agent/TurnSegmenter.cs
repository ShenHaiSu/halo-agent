namespace HaloAgent.Engine.Core.Agent;

using HaloAgent.Engine.Core.Llm;

/// <summary>
/// 回合（Turn）聚合并切分器。
/// 保证切分后保留完整回合，杜绝因硬切消息数导致的孤儿工具调用或半截回复。
/// </summary>
public sealed class TurnSegmenter
{
    public const int DefaultKeepTailTurns = 2;

    /// <summary>
    /// 将消息列表（不含头部 System Prompt）按 Turn 聚合并切分。
    /// </summary>
    public static (List<List<ChatMessage>> OldTurns, List<List<ChatMessage>> KeepTurns)
        Segment(IReadOnlyList<ChatMessage> nonSystemMessages, int keepTailTurns = DefaultKeepTailTurns)
    {
        // 1. 按 TurnId 分组（保持原有顺序）
        var turns = new List<List<ChatMessage>>();
        List<ChatMessage>? currentTurn = null;
        string? currentTurnId = null;

        foreach (var msg in nonSystemMessages)
        {
            if (msg.IsCompactMarker)
            {
                if (currentTurn != null)
                {
                    currentTurn.Add(msg);
                }
                else
                {
                    currentTurn = new List<ChatMessage> { msg };
                    turns.Add(currentTurn);
                }
                currentTurnId = null;
                continue;
            }

            // 遇到新的 TurnId 或新的 Ord=0 User 消息，开启新回合
            bool isNewTurn = (!string.IsNullOrEmpty(msg.TurnId) && msg.TurnId != currentTurnId)
                             || (msg.Role == Role.User && msg.Ord == 0 && currentTurn != null && currentTurn.Count > 0);

            if (isNewTurn || currentTurn == null)
            {
                currentTurn = new List<ChatMessage>();
                turns.Add(currentTurn);
                currentTurnId = msg.TurnId;
            }
            currentTurn.Add(msg);
        }

        // 2. 统计真实回合数（排除纯 marker 回合）
        int realTurnCount = turns.Count(t => t.Any(m => !m.IsCompactMarker));
        if (realTurnCount <= keepTailTurns)
        {
            return (new List<List<ChatMessage>>(), turns);
        }

        // 3. 回合级切分：保留最后 keepTailTurns 个真实回合
        int realSeenFromEnd = 0;
        int keepStartIndex = turns.Count;
        for (int i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Any(m => !m.IsCompactMarker))
            {
                realSeenFromEnd++;
                if (realSeenFromEnd == keepTailTurns)
                {
                    keepStartIndex = i;
                    break;
                }
            }
        }

        var oldTurns = turns.Take(keepStartIndex).ToList();
        var keepTurns = turns.Skip(keepStartIndex).ToList();

        return (oldTurns, keepTurns);
    }
}
