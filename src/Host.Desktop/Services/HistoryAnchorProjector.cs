using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 摘要消息锚点投影：把展示用 turnId 映射为持久化消息主键。
/// 前端不解析内部 ID 格式，投影只在摘要读出时执行，不触及 SQLite 结构。
/// </summary>
public static class HistoryAnchorProjector
{
    private const string UserTurnPrefix = "uturn-";
    private const string CompactTurnPrefix = "cturn-";
    private const string BranchSuffix = ":branch:";
    private const string CompactBlockPrefix = "compact:";

    /// <summary>用消息引用列表填充摘要的操作锚点，返回同一批摘要实例。</summary>
    public static IReadOnlyList<TurnSummary> Project(
        IReadOnlyList<TurnSummary> summaries,
        IReadOnlyList<HistoryMessageRef> messageRefs
    )
    {
        if (summaries == null)
            return Array.Empty<TurnSummary>();
        if (messageRefs == null || messageRefs.Count == 0)
        {
            foreach (var summary in summaries)
            {
                if (summary == null)
                    continue;
                summary.UserMessageId = null;
                summary.AssistantMessageId = null;
            }
            return summaries;
        }

        var byId = new Dictionary<string, HistoryMessageRef>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < messageRefs.Count; i++)
        {
            var reference = messageRefs[i];
            if (reference == null || string.IsNullOrWhiteSpace(reference.Id))
                continue;
            if (!byId.ContainsKey(reference.Id))
                byId[reference.Id] = reference;
        }

        foreach (var summary in summaries)
        {
            if (summary == null)
                continue;
            summary.UserMessageId = null;
            summary.AssistantMessageId = null;
            NormalizeKind(summary);

            string? candidate = ExtractCandidateMessageId(summary.TurnId);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (!byId.TryGetValue(candidate, out var userRef) || !IsRole(userRef.Role, "user"))
                continue;

            summary.UserMessageId = userRef.Id;
            if (IsCompact(summary))
                continue;
            if (
                !HistoryTurnSlice.TryResolve(messageRefs, candidate, out var slice)
                || slice.UserIndex < 0
                || slice.EndExclusive <= slice.UserIndex
            )
                continue;

            for (int i = slice.EndExclusive - 1; i >= slice.UserIndex; i--)
            {
                if (IsRole(messageRefs[i].Role, "assistant"))
                {
                    summary.AssistantMessageId = messageRefs[i].Id;
                    break;
                }
            }
        }
        return summaries;
    }

    private static void NormalizeKind(TurnSummary summary)
    {
        if (IsCompact(summary))
            return;
        string turnId = summary.TurnId ?? string.Empty;
        string finalBlockId = summary.FinalBlockId ?? string.Empty;
        if (
            turnId.StartsWith(CompactTurnPrefix, StringComparison.OrdinalIgnoreCase)
            || finalBlockId.StartsWith(CompactBlockPrefix, StringComparison.OrdinalIgnoreCase)
        )
            summary.Kind = "compact";
    }

    private static bool IsCompact(TurnSummary summary) =>
        string.Equals(summary.Kind, "compact", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractCandidateMessageId(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return null;
        string value = turnId.Trim();
        string candidate;
        if (value.StartsWith(UserTurnPrefix, StringComparison.OrdinalIgnoreCase))
            candidate = value.Substring(UserTurnPrefix.Length);
        else if (value.StartsWith(CompactTurnPrefix, StringComparison.OrdinalIgnoreCase))
            candidate = value.Substring(CompactTurnPrefix.Length);
        else
            return null;
        int branchSep = candidate.IndexOf(BranchSuffix, StringComparison.OrdinalIgnoreCase);
        if (branchSep >= 0)
            candidate = candidate.Substring(0, branchSep);
        return candidate.Length == 0 ? null : candidate;
    }

    private static bool IsRole(string? role, string expected) =>
        string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);
}
