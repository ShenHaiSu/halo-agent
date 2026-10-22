using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-3：压缩切点（token 切点，替换按组数逻辑）。
/// FindCutPoint 从尾按 token 累积至 keepRecent，落到合法切点（不断工具链）；
/// 孤儿 toolCall 剪除；单消息截断；retained 只留 active 最新 user 防孤儿 toolCall。
/// </summary>
public static class CompactionPlan
{
    /// <summary>摘要分片上限（≤16 块）。</summary>
    public const int MaxSummaryChunks = 16;

    /// <summary>toolResult 降级截断字符数（+ 后缀）。</summary>
    public const int ToolResultReduceChars = 500;

    /// <summary>toolResult 降级后缀。</summary>
    public const string ToolResultTruncatedSuffix = "…[truncated]";

    /// <summary>单消息截断下限字符数（≥64）。</summary>
    public const int SingleMessageMinChars = 64;

    /// <summary>摘要 marker 前缀（与压缩重写口径一致）。</summary>
    public const string SummaryMarker = "【上下文摘要";

    /// <summary>
    /// 压缩前检查：空 → 跳过；尾已 checkpoint（摘要后无追问）→ 跳过；
    /// 上 checkpoint 尾作 virtual entries（计入 anchor，不再压缩）。
    /// </summary>
    public static (bool Skip, string Reason) PrepareCompaction(
        IReadOnlyList<UnifiedChatMessage>? rest
    )
    {
        if (rest == null || rest.Count == 0)
            return (true, "EMPTY");
        if (SystemTranscriptCheckpoint.IsTailCheckpointed(rest))
            return (true, "TAIL_CHECKPOINTED");
        return (false, string.Empty);
    }

    /// <summary>上个 checkpoint 摘要在 rest 中的下标（virtual entries 尾），无则 -1。</summary>
    public static int FindPrevSummaryIndex(IReadOnlyList<UnifiedChatMessage>? rest)
    {
        if (rest == null)
            return -1;
        for (int i = rest.Count - 1; i >= 0; i--)
        {
            var m = rest[i];
            if (
                m != null
                && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                && (m.Content ?? string.Empty).Contains(SummaryMarker, StringComparison.Ordinal)
            )
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 从尾按 EstimateTokens 累积至 keepRecent，落到合法切点（不断工具链）。
    /// maxTailGroups &gt; 0 时再与“末 N 原子组”取交（取更小的尾巴），兼容渐进裁剪。
    /// 返回 tail 起始下标（[cut..) 为保留尾部）。
    /// </summary>
    public static int FindCutPoint(
        IReadOnlyList<UnifiedChatMessage> messages,
        int keepRecentTokens,
        int maxTailGroups = 0
    )
    {
        if (messages == null || messages.Count == 0)
            return 0;
        if (keepRecentTokens < 1)
            keepRecentTokens = 1;
        int n = messages.Count;
        // 原子单元：assistant(toolCalls)+后续 tool 为不可分割单元，其余单条成单元。
        var units = BuildAtomicUnits(messages);
        long acc = 0;
        int cut = n;
        for (int u = units.Count - 1; u >= 0; u--)
        {
            long tok = units[u].Tokens;
            if (acc + tok > keepRecentTokens && cut < n)
                break;
            acc += tok;
            cut = units[u].Start;
            if (acc >= keepRecentTokens)
                break;
        }
        // 若全部可容纳：cut=0（全留）；调用方可再按组数上限收紧。
        if (maxTailGroups > 0)
        {
            int groupCut = TailGroupsCut(messages, maxTailGroups);
            if (groupCut > cut)
                cut = groupCut;
        }
        cut = AdjustToLegalBoundary(messages, cut);
        return Math.Clamp(cut, 0, n);
    }

    /// <summary>
    /// 取最近连续 user/assistant/toolResult 窗 + 去孤儿 + 单消息截断。
    /// latestUser 恒保留（retained 只留 active 最新 user，防孤儿 toolCall）。
    /// </summary>
    public static List<UnifiedChatMessage> SelectRecentTail(
        IReadOnlyList<UnifiedChatMessage> messages,
        int cutIndex,
        int retainedUserChars = 0
    )
    {
        var tail = new List<UnifiedChatMessage>();
        if (messages == null)
            return tail;
        for (int i = Math.Max(0, cutIndex); i < messages.Count; i++)
        {
            var m = messages[i];
            if (m == null)
                continue;
            tail.Add(CloneMessage(m));
        }
        tail = PruneOrphanToolResults(tail);
        // 最新 user 恒保留：若被截断逻辑影响则优先保全。
        int latestUser = -1;
        for (int i = tail.Count - 1; i >= 0; i--)
        {
            if (string.Equals(tail[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                latestUser = i;
                break;
            }
        }
        if (retainedUserChars > 0)
        {
            for (int i = 0; i < tail.Count; i++)
            {
                if (i == latestUser)
                    continue;
                tail[i] = TruncateLongMessage(tail[i], retainedUserChars);
            }
        }
        return tail;
    }

    /// <summary>去孤儿 tool 结果：无归属 assistant tool_call 的 tool 消息直接剪除（同留同删）。</summary>
    public static List<UnifiedChatMessage> PruneOrphanToolResults(List<UnifiedChatMessage> tail)
    {
        if (tail == null || tail.Count == 0)
            return new List<UnifiedChatMessage>();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in tail)
        {
            if (
                m != null
                && string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.ToolCalls != null
            )
            {
                foreach (var c in m.ToolCalls)
                {
                    if (c != null && !string.IsNullOrWhiteSpace(c.CallId))
                        owned.Add(c.CallId);
                }
            }
        }
        // 若整窗无 assistant 链（如纯 user 尾），tool 消息整体视为孤儿剪除。
        return tail.Where(m =>
            {
                if (m == null)
                    return false;
                if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrWhiteSpace(m.ToolCallId)
                        && owned.Contains(m.ToolCallId!);
                return true;
            })
            .ToList();
    }

    /// <summary>
    /// 单消息截断到尾部：chars = tok×3.5（≥64）。保留尾部字符 + 截断前缀标记，保证仍合法。
    /// </summary>
    public static UnifiedChatMessage TruncateMessageToTail(UnifiedChatMessage message, int chars)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
        int keep = Math.Max(SingleMessageMinChars, chars);
        var copy = CloneMessage(message);
        copy.Content = TruncateToTail(copy.Content, keep);
        copy.ReasoningContent = TruncateToTail(copy.ReasoningContent, keep);
        return copy;
    }

    /// <summary>合法边界：切点必须落在 user/assistant/custom/bash/branchSummary 起始；绝不切在 tool 块中间。</summary>
    public static bool IsLegalCutBoundary(IReadOnlyList<UnifiedChatMessage> messages, int cut)
    {
        if (messages == null)
            return cut == 0;
        if (cut <= 0 || cut >= messages.Count)
            return true;
        var m = messages[cut];
        if (m == null)
            return false;
        if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
            return false;
        if (cut > 0)
        {
            var prev = messages[cut - 1];
            if (
                prev != null
                && string.Equals(prev.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && prev.ToolCalls != null
                && prev.ToolCalls.Count > 0
                && string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            )
                return false;
        }
        string role = (m.Role ?? string.Empty).Trim().ToLowerInvariant();
        return role
            is "user"
                or "assistant"
                or "system"
                or "custom"
                or "bash"
                or "branchsummary"
                or "branch_summary";
    }

    /// <summary>最新 user 目标（latestUserGoal）：尾部最后一个 user 消息，另保半预算 user 场景用。</summary>
    public static UnifiedChatMessage? FindLatestUserGoal(IReadOnlyList<UnifiedChatMessage>? tail)
    {
        if (tail == null)
            return null;
        for (int i = tail.Count - 1; i >= 0; i--)
        {
            var m = tail[i];
            if (m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }

    // ---------- 内部 ----------

    private sealed record AtomicUnit(int Start, int End, long Tokens);

    private static List<AtomicUnit> BuildAtomicUnits(IReadOnlyList<UnifiedChatMessage> messages)
    {
        var units = new List<AtomicUnit>();
        int i = 0;
        while (i < messages.Count)
        {
            var m = messages[i];
            if (
                m != null
                && string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.ToolCalls != null
                && m.ToolCalls.Count > 0
            )
            {
                int start = i;
                long tok = TokenBudgetService.EstimateMessageTokens(m);
                i++;
                while (
                    i < messages.Count
                    && messages[i] != null
                    && string.Equals(messages[i].Role, "tool", StringComparison.OrdinalIgnoreCase)
                )
                {
                    tok += TokenBudgetService.EstimateMessageTokens(messages[i]);
                    i++;
                }
                units.Add(new AtomicUnit(start, i, tok));
            }
            else
            {
                long tok = m == null ? 0 : TokenBudgetService.EstimateMessageTokens(m);
                units.Add(new AtomicUnit(i, i + 1, tok));
                i++;
            }
        }
        return units;
    }

    private static int TailGroupsCut(IReadOnlyList<UnifiedChatMessage> messages, int maxTailGroups)
    {
        var groups = new TokenBudgetService().GroupIntoAtomicTurns(messages);
        if (groups.Count == 0)
            return messages.Count;
        int take = Math.Min(maxTailGroups, groups.Count);
        int skip = 0;
        for (int i = 0; i < groups.Count - take; i++)
            skip += groups[i].Messages.Count;
        return Math.Clamp(skip, 0, messages.Count);
    }

    private static int AdjustToLegalBoundary(IReadOnlyList<UnifiedChatMessage> messages, int cut)
    {
        int n = messages.Count;
        while (cut > 0 && cut < n)
        {
            var m = messages[cut];
            if (m != null && !string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
                break;
            // 切在 tool 块内：整条链前移（同留）。
            int j = cut - 1;
            while (
                j >= 0
                && messages[j] != null
                && string.Equals(messages[j].Role, "tool", StringComparison.OrdinalIgnoreCase)
            )
                j--;
            cut = Math.Max(0, j);
            // 若落到带 toolCalls 的 assistant 上且其 tool 结果在切点后，整链前移。
            if (
                cut < n
                && messages[cut] != null
                && string.Equals(
                    messages[cut].Role,
                    "assistant",
                    StringComparison.OrdinalIgnoreCase
                )
                && messages[cut].ToolCalls != null
                && messages[cut].ToolCalls!.Count > 0
            )
            {
                // assistant+tool 同留：切点移到该 assistant 之前（已是），合法。
                break;
            }
            if (cut == 0)
                break;
        }
        return cut;
    }

    private static UnifiedChatMessage TruncateLongMessage(UnifiedChatMessage m, int chars)
    {
        int total = (m.Content?.Length ?? 0) + (m.ReasoningContent?.Length ?? 0);
        if (total <= chars)
            return m;
        return TruncateMessageToTail(m, chars);
    }

    private static string? TruncateToTail(string? text, int keep)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= keep)
            return text;
        return "…" + text.Substring(text.Length - keep);
    }

    private static UnifiedChatMessage CloneMessage(UnifiedChatMessage m)
    {
        return new UnifiedChatMessage
        {
            Id = m.Id,
            CreatedAtUtc = m.CreatedAtUtc,
            Role = m.Role,
            Content = m.Content,
            ReasoningContent = m.ReasoningContent,
            ToolCalls =
                m.ToolCalls != null
                    ? new List<LlmGateway.Models.UnifiedToolCall>(m.ToolCalls)
                    : null,
            ToolCallId = m.ToolCallId,
            Name = m.Name,
            InputTokens = m.InputTokens,
            OutputTokens = m.OutputTokens,
            CachedTokens = m.CachedTokens,
            Images = m.Images,
        };
    }
}
