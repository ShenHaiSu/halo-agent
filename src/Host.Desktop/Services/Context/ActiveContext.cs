using Host.Desktop.Core.Models;

namespace Host.Desktop.Services.Context;

/// <summary>
/// P007：活跃切片（真正进模型上下文的那段）。唯一定义处——
/// 其它层（TurnPlanner / TokenBudget / Compaction）只允许调用本类，禁止各自写反向扫描。
/// 纯函数、无 IO、无状态；可对任意列表直接单测。
/// </summary>
public static class ActiveContext
{
    /// <summary>压缩前的最小活跃条数（低于此不做压缩，避免把一次问答压成摘要）。</summary>
    public const int MinCompactMessages = 6;

    /// <summary>最后一个桩的下标；无桩返回 -1。判定只认 kind（老数据兼容层见 LegacyKinds）。</summary>
    public static int FindLastStubIndex(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        if (messages == null)
            return -1;
        for (int i = messages.Count - 1; i >= 0; i--)
            if (IsStub(messages[i]))
                return i;
        return -1;
    }

    /// <summary>活跃切片起始下标（含桩）。无桩返回 0（恒等）。</summary>
    public static int ResolveStartIndex(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        var all = messages ?? Array.Empty<UnifiedChatMessage>();
        if (all.Count == 0)
            return 0;
        int stub = FindLastStubIndex(all);
        if (stub <= 0)
            return 0; // 无桩 / 桩在 0 → 全量
        int lead = LeadingSystemCount(all);
        return lead > stub ? 0 : stub; // 异常数据（前导 system 越过桩）→ 回退全量
    }

    /// <summary>
    /// 切片 = [0..lead) ∪ [stub..n)。返回新列表（元素是原对象引用，不改输入）。
    /// </summary>
    public static List<UnifiedChatMessage> Slice(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        var all = messages ?? Array.Empty<UnifiedChatMessage>();
        int start = ResolveStartIndex(all);
        if (start == 0)
            return new List<UnifiedChatMessage>(all);
        int lead = LeadingSystemCount(all);
        var result = new List<UnifiedChatMessage>(lead + all.Count - start);
        for (int i = 0; i < lead && i < start; i++)
            result.Add(all[i]);
        for (int i = start; i < all.Count; i++)
            result.Add(all[i]);
        return result;
    }

    /// <summary>切片是否需要压缩：EMPTY / NO_NEW_CONTENT / TOO_SHORT / 空串=可压。</summary>
    public static string PrepareCompactionReason(IReadOnlyList<UnifiedChatMessage>? active)
    {
        if (active == null || active.Count == 0)
            return "EMPTY";
        int stub = FindLastStubIndex(active);
        int body = active.Count - LeadingSystemCount(active);
        if (stub < 0 && body < MinCompactMessages)
            return "TOO_SHORT";
        // 已有桩且桩后只有前导 system（等于没有新内容）→ 无需再压
        if (stub >= 0 && active.Count - stub <= 1)
            return "NO_NEW_CONTENT";
        if (stub >= 0 && body - 1 < MinCompactMessages)
            return "TOO_SHORT";
        return string.Empty;
    }

    public static bool IsStub(UnifiedChatMessage? m)
    {
        if (m == null)
            return false;
        if (string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal))
            return true;
        // 老数据一次性兼容（M5 backfill 后本分支可删；详见 LegacyKinds）
        return false;
    }

    private static int LeadingSystemCount(IReadOnlyList<UnifiedChatMessage> all)
    {
        int lead = 0;
        while (
            lead < all.Count
            && string.Equals(all[lead].Role, "system", StringComparison.OrdinalIgnoreCase)
        )
            lead++;
        return lead;
    }
}
