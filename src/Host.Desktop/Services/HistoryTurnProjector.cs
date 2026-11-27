using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 历史孤儿只读投影：为 messages 中存在、turns 无摘要的非 compact user 合成取消轮。
/// 只改内存 DTO，不写库、不改真实 turn_seq；桥方法签名与 DTO 形状保持不变。
/// </summary>
internal static class HistoryTurnProjector
{
    private const string UserTurnPrefix = "uturn-";
    private const string CompactStubKind = "compact_stub";
    private const string LegacyCompactContentPrefix = "【上下文摘要";

    /// <summary>收集已被现有摘要表示的用户消息主键（含 cturn / branch 后缀）。</summary>
    public static HashSet<string> CollectRepresentedUserIds(IReadOnlyList<TurnSummary> summaries)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (summaries == null)
            return result;
        foreach (var summary in summaries)
        {
            if (summary == null)
                continue;
            string? candidate = HistoryAnchorProjector.ExtractCandidateMessageId(summary.TurnId);
            if (!string.IsNullOrWhiteSpace(candidate))
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// 从消息引用中筛出缺失的普通 user：排除工具/助手、compact stub 与已被摘要表示的消息。
    /// legacy 文本前缀的 stub 没有 content 字段可判，在仓储查询层按正文排除。
    /// </summary>
    public static List<HistoryMessageRef> FindMissingUserRefs(
        IReadOnlyList<HistoryMessageRef> messageRefs,
        IReadOnlyCollection<string> representedUserIds
    )
    {
        var result = new List<HistoryMessageRef>();
        if (messageRefs == null)
            return result;
        var represented =
            representedUserIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < messageRefs.Count; i++)
        {
            var reference = messageRefs[i];
            if (reference == null || string.IsNullOrWhiteSpace(reference.Id))
                continue;
            if (!string.Equals(reference.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(reference.Kind, CompactStubKind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (represented.Contains(reference.Id))
                continue;
            result.Add(reference);
        }
        return result;
    }

    /// <summary>
    /// 合并现有摘要与合成取消轮：跳过活跃轮最新 user，按消息位置排序并对本次响应重编号。
    /// </summary>
    public static IReadOnlyList<TurnSummary> ProjectMissingUserTurns(
        IReadOnlyList<TurnSummary> summaries,
        IReadOnlyList<HistoryMessageRef> messageRefs,
        IReadOnlyList<HistoryUserTurn> missingUsers,
        bool hasActiveAgentTurn
    )
    {
        var existing =
            summaries == null ? new List<TurnSummary>() : new List<TurnSummary>(summaries);
        var refs = messageRefs ?? Array.Empty<HistoryMessageRef>();
        var orphans = missingUsers ?? Array.Empty<HistoryUserTurn>();

        // 消息位置：以 id 在 messageRefs 中的序位表达真实消息顺序（OrdinalIgnoreCase）。
        var positionById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < refs.Count; i++)
        {
            var reference = refs[i];
            if (reference == null || string.IsNullOrWhiteSpace(reference.Id))
                continue;
            if (!positionById.ContainsKey(reference.Id))
                positionById[reference.Id] = i;
        }

        // 活跃轮最新 user：只跳过它本身，更早的孤儿仍投影。
        string? activeLatestUserId = null;
        if (hasActiveAgentTurn)
        {
            for (int i = refs.Count - 1; i >= 0; i--)
            {
                var reference = refs[i];
                if (
                    reference != null
                    && string.Equals(reference.Role, "user", StringComparison.OrdinalIgnoreCase)
                )
                {
                    activeLatestUserId = reference.Id;
                    break;
                }
            }
        }

        var entries = new List<Entry>(existing.Count + orphans.Count);
        int fallbackPosition = refs.Count;
        for (int i = 0; i < existing.Count; i++)
        {
            var summary = existing[i];
            if (summary == null)
                continue;
            entries.Add(
                new Entry(ResolvePosition(summary, positionById, fallbackPosition), i, summary)
            );
        }

        for (int i = 0; i < orphans.Count; i++)
        {
            var orphan = orphans[i];
            if (orphan == null || string.IsNullOrWhiteSpace(orphan.Id))
                continue;
            if (IsLegacyCompactContent(orphan.Content))
                continue;
            if (
                activeLatestUserId != null
                && string.Equals(activeLatestUserId, orphan.Id, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            entries.Add(
                new Entry(
                    positionById.TryGetValue(orphan.Id, out var pos) ? pos : fallbackPosition,
                    existing.Count + i,
                    Synthesize(orphan)
                )
            );
        }

        // 稳定排序：无法定位的排在已定位项之后，相同位置维持原有相对顺序。
        entries.Sort(
            static (a, b) =>
            {
                int cmp = a.Position.CompareTo(b.Position);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            }
        );

        var result = new List<TurnSummary>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var summary = entries[i].Summary;
            summary.TurnSeq = i + 1; // 只改本次响应 DTO，不回写数据库。
            result.Add(summary);
        }
        return result;
    }

    /// <summary>按锚点投影后的 UserMessageId 或 turnId 候选定位消息位置。</summary>
    private static int ResolvePosition(
        TurnSummary summary,
        IReadOnlyDictionary<string, int> positionById,
        int fallbackPosition
    )
    {
        if (
            !string.IsNullOrWhiteSpace(summary.UserMessageId)
            && positionById.TryGetValue(summary.UserMessageId, out var byAnchor)
        )
            return byAnchor;
        string? candidate = HistoryAnchorProjector.ExtractCandidateMessageId(summary.TurnId);
        if (
            !string.IsNullOrWhiteSpace(candidate)
            && positionById.TryGetValue(candidate, out var byCandidate)
        )
            return byCandidate;
        return fallbackPosition;
    }

    private static TurnSummary Synthesize(HistoryUserTurn user)
    {
        string turnId = UserTurnPrefix + user.Id;
        string created = user.CreatedAtUtc ?? string.Empty;
        return new TurnSummary
        {
            TurnId = turnId,
            TurnSeq = 0,
            Kind = "turn",
            IterEnd = 0,
            UserPreview = TimelineBlockTracker.BuildPreview(user.Content ?? string.Empty, 15),
            UserContent = user.Content ?? string.Empty,
            FinalBlockId = null,
            Blocks = 0,
            Tools = 0,
            Failed = 0,
            DurationMs = 0,
            Chars = 0,
            Status = TimelineTurnStatus.Cancelled,
            LastRequestId = string.Empty,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            AnchorKey = turnId,
            RemovedMessages = 0,
            UserMessageId = user.Id,
            AssistantMessageId = null,
        };
    }

    private static bool IsLegacyCompactContent(string? content) =>
        content != null && content.StartsWith(LegacyCompactContentPrefix, StringComparison.Ordinal);

    private readonly struct Entry
    {
        public Entry(int position, int order, TurnSummary summary)
        {
            Position = position;
            Order = order;
            Summary = summary;
        }

        public int Position { get; }
        public int Order { get; }
        public TurnSummary Summary { get; }
    }
}
