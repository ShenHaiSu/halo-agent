using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>统一轮次切面：按用户消息边界计算重放与分支共享的整轮范围。</summary>
public readonly record struct HistoryTurnSlice(int UserIndex, int EndExclusive)
{
    public static bool TryResolve(
        IReadOnlyList<HistoryMessageRef> messages,
        string? anchorMessageId,
        out HistoryTurnSlice slice
    ) => TryResolveCore(messages, m => m.Id, m => m.Role, anchorMessageId, out slice);

    public static bool TryResolve(
        IReadOnlyList<UnifiedChatMessage> messages,
        string? anchorMessageId,
        out HistoryTurnSlice slice
    ) => TryResolveCore(messages, m => m.Id, m => m.Role, anchorMessageId, out slice);

    private static bool TryResolveCore<T>(
        IReadOnlyList<T> messages,
        Func<T, string?> idSelector,
        Func<T, string?> roleSelector,
        string? anchorMessageId,
        out HistoryTurnSlice slice
    )
    {
        slice = default;
        if (messages == null || messages.Count == 0 || string.IsNullOrWhiteSpace(anchorMessageId))
            return false;

        int anchorIndex = -1;
        for (int i = 0; i < messages.Count; i++)
        {
            if (
                string.Equals(
                    idSelector(messages[i]),
                    anchorMessageId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                anchorIndex = i;
                break;
            }
        }
        if (anchorIndex < 0)
            return false;

        int userIndex = anchorIndex;
        if (!IsUser(roleSelector(messages[anchorIndex])))
        {
            for (int i = anchorIndex - 1; i >= 0; i--)
            {
                if (IsUser(roleSelector(messages[i])))
                {
                    userIndex = i;
                    break;
                }
            }
            if (!IsUser(roleSelector(messages[userIndex])))
                return false;
        }

        int endExclusive = messages.Count;
        for (int i = userIndex + 1; i < messages.Count; i++)
        {
            if (IsUser(roleSelector(messages[i])))
            {
                endExclusive = i;
                break;
            }
        }

        slice = new HistoryTurnSlice(userIndex, endExclusive);
        return true;
    }

    private static bool IsUser(string? role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
}
