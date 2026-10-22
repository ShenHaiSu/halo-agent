using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-1：SystemTranscriptJournal —— dispatch 前持久化 provider-neutral 声明。
/// 行结构 {id, role:system, version:1, messageJson, before/after}；orderRows 按 before/after 重排 + 去重。
/// persist 失败抛本地错误阻止发送（不污染网关请求）。形状坏的记录直接本地错误（fail fast）。
/// </summary>
public sealed class SystemTranscriptJournal
{
    public const int CurrentVersion = 1;

    private readonly List<TranscriptJournalRow> _rows = new();
    private readonly object _lock = new();
    private long _nextId = 1;

    /// <summary>
    /// 持久化一行声明。role 必须为 system，messageJson 非空；before/after 为前后锚点（可空）。
    /// 校验失败抛 InvalidOperationException（本地错误，阻止发送）。
    /// </summary>
    public TranscriptJournalRow Persist(
        string role,
        string messageJson,
        string? before = null,
        string? after = null
    )
    {
        if (string.IsNullOrWhiteSpace(role))
            throw new InvalidOperationException("TRANSCRIPT_JOURNAL_BAD_ROW: role required");
        if (!string.Equals(role.Trim(), "system", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "TRANSCRIPT_JOURNAL_BAD_ROW: only system rows are journaled"
            );
        if (string.IsNullOrWhiteSpace(messageJson))
            throw new InvalidOperationException("TRANSCRIPT_JOURNAL_BAD_ROW: messageJson required");
        lock (_lock)
        {
            var row = new TranscriptJournalRow(
                _nextId++,
                role.Trim().ToLowerInvariant(),
                CurrentVersion,
                messageJson,
                before,
                after
            );
            _rows.Add(row);
            return row;
        }
    }

    /// <summary>按 before/after 链重排 + 按 id 去重（保留首个）。无锚点行按 id 追加在后。</summary>
    public IReadOnlyList<TranscriptJournalRow> OrderRows()
    {
        List<TranscriptJournalRow> rows;
        lock (_lock)
            rows = _rows.ToList();
        var seen = new HashSet<long>();
        var deduped = new List<TranscriptJournalRow>();
        foreach (var r in rows.OrderBy(r => r.Id))
        {
            if (seen.Add(r.Id))
                deduped.Add(r);
        }
        // 起点：before 为空（或 before 不指向任何 after）的行，按 id 排序。
        var afters = new HashSet<string>(
            deduped.Where(r => !string.IsNullOrEmpty(r.After)).Select(r => r.After!),
            StringComparer.Ordinal
        );
        var byBefore = deduped
            .Where(r => !string.IsNullOrEmpty(r.Before))
            .GroupBy(r => r.Before!)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToList(), StringComparer.Ordinal);
        var ordered = new List<TranscriptJournalRow>();
        var used = new HashSet<long>();
        var starts = deduped
            .Where(r => string.IsNullOrEmpty(r.Before) || !afters.Contains(r.Before!))
            .OrderBy(r => r.Id)
            .ToList();
        foreach (var s in starts)
        {
            TranscriptJournalRow? cur = s;
            while (cur != null && used.Add(cur.Id))
            {
                ordered.Add(cur);
                TranscriptJournalRow? next = null;
                if (
                    !string.IsNullOrEmpty(cur.After)
                    && byBefore.TryGetValue(cur.After!, out var cands)
                )
                    next = cands.FirstOrDefault(c => !used.Contains(c.Id));
                cur = next;
            }
        }
        // 孤儿行（成环或断链）按 id 追加，保证不丢。
        foreach (var r in deduped.OrderBy(r => r.Id))
        {
            if (used.Add(r.Id))
                ordered.Add(r);
        }
        return ordered;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _rows.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _rows.Clear();
            _nextId = 1;
        }
    }
}

/// <summary>journal 行：{id, role:system, version:1, messageJson, before/after}。</summary>
public sealed record TranscriptJournalRow(
    long Id,
    string Role,
    int Version,
    string MessageJson,
    string? Before,
    string? After
);

/// <summary>
/// bigChange P1-1：checkpoint / rebuild —— 剥瞬时与恢复保序。
/// SystemTranscriptCheckpoint() 剥掉 context（水位瞬时）再进摘要输入；
/// RebuildSystemTranscript() 把系统行按前后锚点插回原相对位置，永不跑到 user/assistant 中间打散前缀。
/// RemoveTrailingAssistantMessages：失败恢复丢尾部 assistant（跳过 system）。
/// </summary>
public static class SystemTranscriptCheckpoint
{
    public const string SummaryMarker = "【上下文摘要";

    /// <summary>剥掉 context（水位瞬时）再进摘要输入。</summary>
    public static SystemTranscript.TranscriptState Strip(SystemTranscript.TranscriptState state) =>
        SystemTranscript.Checkpoint(state);

    /// <summary>把系统行按前后锚点插回原相对位置（context 恒为尾部）。</summary>
    public static SystemTranscript.TranscriptState Rebuild(
        SystemTranscript.TranscriptState checkpoint,
        string? contextSection
    ) => SystemTranscript.Rebuild(checkpoint, contextSection);

    /// <summary>
    /// 把系统行插回消息序列原相对位置：首部连续 system 行之后、首个 user/assistant 之前；
    /// 永不打散到 user/assistant 中间。返回新列表（不改输入）。
    /// </summary>
    public static List<UnifiedChatMessage> RebuildSystemTranscript(
        IReadOnlyList<UnifiedChatMessage>? messages,
        IReadOnlyList<UnifiedChatMessage>? systemRows
    )
    {
        var rest = (messages ?? Array.Empty<UnifiedChatMessage>()).Where(m => m != null).ToList();
        var sys = (systemRows ?? Array.Empty<UnifiedChatMessage>())
            .Where(m =>
                m != null && string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        // 移除现存首部 system 行（由 journal 重建统一口径），其余相对位置不变。
        int lead = 0;
        while (
            lead < rest.Count
            && string.Equals(rest[lead].Role, "system", StringComparison.OrdinalIgnoreCase)
        )
            lead++;
        var result = new List<UnifiedChatMessage>(sys.Count + rest.Count);
        result.AddRange(sys);
        result.AddRange(rest.Skip(lead));
        return result;
    }

    /// <summary>失败恢复：丢尾部 assistant（跳过 system，system 永不丢）。</summary>
    public static List<UnifiedChatMessage> RemoveTrailingAssistantMessages(
        IReadOnlyList<UnifiedChatMessage>? messages
    )
    {
        var list = (messages ?? Array.Empty<UnifiedChatMessage>()).Where(m => m != null).ToList();
        int end = list.Count;
        while (end > 0)
        {
            var m = list[end - 1];
            if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                end--;
            else
                break;
        }
        if (end == list.Count)
            return list;
        return list.Take(end).ToList();
    }

    /// <summary>尾部是否已是 checkpoint（摘要 marker 之后无有效 user 追问）。</summary>
    public static bool IsTailCheckpointed(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        if (messages == null || messages.Count == 0)
            return true;
        int lastSummary = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m == null)
                continue;
            if (
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                && (m.Content ?? string.Empty).Contains(SummaryMarker, StringComparison.Ordinal)
            )
            {
                lastSummary = i;
                break;
            }
        }
        if (lastSummary < 0)
            return false;
        for (int i = lastSummary + 1; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m == null)
                continue;
            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
