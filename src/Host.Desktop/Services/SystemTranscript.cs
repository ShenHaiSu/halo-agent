using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-1：System Transcript —— 系统提示词按写死顺序拼接的分节模型。
/// 顺序：runtime → tool_activation → skills(名排序，各 skill 独立 section) → context(易变尾部)。
/// 同一会话连续两轮：前 N 字节完全相同（易变只在尾部）。变量（路径/mode/日期/水位）一律放段尾。
/// </summary>
public static class SystemTranscript
{
    public const string RuntimeSection = "runtime";
    public const string ToolActivationSection = "tool_activation";
    public const string ContextSection = "context";

    /// <summary>skill 独立 section 名前缀：skill:&lt;name&gt;。</summary>
    public const string SkillSectionPrefix = "skill:";

    public const long InitialTimestamp = 1;

    /// <summary>全局单调时钟下限：timestamp 永不回拨（跨会话亦然）。</summary>
    private static long s_floor;

    private static readonly ConcurrentDictionary<string, TranscriptState> SessionStates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 分节快照：Sections 按名寻址；Order 为写死顺序；Timestamp 单调增；Text 为装配结果。
    /// 无变化时 Sync 返回原引用（上层借此跳过重建）。
    /// </summary>
    public sealed record TranscriptState(
        IReadOnlyDictionary<string, string> Sections,
        IReadOnlyList<string> Order,
        long Timestamp,
        string Text
    );

    /// <summary>
    /// 写死顺序：runtime → tool_activation → skills(名排序) → context。
    /// 未知扩展 section 一律插到 context 之前（永排桌面自有 sections 之后）。
    /// </summary>
    public static IReadOnlyList<string> SectionOrder(
        IReadOnlyDictionary<string, string>? sections = null
    )
    {
        var order = new List<string> { RuntimeSection, ToolActivationSection };
        if (sections != null)
        {
            foreach (
                string name in sections
                    .Keys.Where(k =>
                        k.StartsWith(SkillSectionPrefix, StringComparison.Ordinal)
                        && k.Length > SkillSectionPrefix.Length
                    )
                    .OrderBy(k => k, StringComparer.Ordinal)
            )
                order.Add(name);
        }
        if (sections != null)
        {
            foreach (
                string name in sections
                    .Keys.Where(k =>
                        !string.Equals(k, RuntimeSection, StringComparison.Ordinal)
                        && !string.Equals(k, ToolActivationSection, StringComparison.Ordinal)
                        && !string.Equals(k, ContextSection, StringComparison.Ordinal)
                        && !k.StartsWith(SkillSectionPrefix, StringComparison.Ordinal)
                    )
                    .OrderBy(k => k, StringComparer.Ordinal)
            )
                order.Add(name);
        }
        order.Add(ContextSection);
        return order;
    }

    /// <summary>按写死顺序 Join("\n\n")；空白 section 整块丢弃。</summary>
    public static string BuildSystemText(
        IReadOnlyDictionary<string, string>? sections,
        bool trailingNewline = false
    )
    {
        if (sections == null || sections.Count == 0)
            return string.Empty;
        var order = SectionOrder(sections);
        var parts = new List<string>(order.Count);
        foreach (string name in order)
        {
            if (sections.TryGetValue(name, out string? text) && !string.IsNullOrWhiteSpace(text))
                parts.Add(text.TrimEnd());
        }
        if (parts.Count == 0)
            return string.Empty;
        string joined = string.Join("\n\n", parts);
        return trailingNewline ? joined + "\n" : joined;
    }

    /// <summary>
    /// 只 diff 有变 section；有 diff 则 append 新快照（timestamp = max+1）；
    /// 无 diff 返回原引用。timestamp 单调增永不回拨。
    /// </summary>
    public static (TranscriptState State, bool Changed) SyncSystemSections(
        TranscriptState? current,
        IReadOnlyDictionary<string, string>? desired
    )
    {
        desired ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (current != null && SectionsEqual(current.Sections, desired))
            return (current, false);
        var order = SectionOrder(desired);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in order)
        {
            if (desired.TryGetValue(name, out string? text) && !string.IsNullOrWhiteSpace(text))
                snap[name] = text;
        }
        long ts = NextTimestamp(current?.Timestamp ?? 0);
        string text2 = BuildSystemText(snap);
        return (new TranscriptState(snap, order, ts, text2), true);
    }

    /// <summary>会话级 Sync：按 sessionId 持有上轮快照，返回（快照，是否变化）。</summary>
    public static (TranscriptState State, bool Changed) SyncForSession(
        string? sessionId,
        IReadOnlyDictionary<string, string>? desired
    )
    {
        string key = string.IsNullOrWhiteSpace(sessionId) ? "-" : sessionId.Trim();
        SessionStates.TryGetValue(key, out TranscriptState? current);
        var (next, changed) = SyncSystemSections(current, desired);
        if (changed)
            SessionStates[key] = next;
        return (next, changed);
    }

    /// <summary>只换 runtime，保留 tool_activation（及其余 sections）；timestamp +1。</summary>
    public static TranscriptState ReplaceSystemPrompt(TranscriptState current, string runtime)
    {
        if (current == null)
            throw new ArgumentNullException(nameof(current));
        var next = new Dictionary<string, string>(current.Sections, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(runtime))
            next.Remove(RuntimeSection);
        else
            next[RuntimeSection] = runtime;
        long ts = NextTimestamp(current.Timestamp);
        return new TranscriptState(next, SectionOrder(next), ts, BuildSystemText(next));
    }

    /// <summary>剥掉 context（水位瞬时）再进摘要输入。</summary>
    public static TranscriptState Checkpoint(TranscriptState current)
    {
        if (current == null)
            throw new ArgumentNullException(nameof(current));
        var next = new Dictionary<string, string>(current.Sections, StringComparer.Ordinal);
        next.Remove(ContextSection);
        long ts = NextTimestamp(current.Timestamp);
        return new TranscriptState(next, SectionOrder(next), ts, BuildSystemText(next));
    }

    /// <summary>
    /// 把系统行按前后锚点插回原相对位置（context 恒为尾部），永不打散到 user/assistant 中间。
    /// </summary>
    public static TranscriptState Rebuild(TranscriptState checkpoint, string? contextSection)
    {
        if (checkpoint == null)
            throw new ArgumentNullException(nameof(checkpoint));
        var next = new Dictionary<string, string>(checkpoint.Sections, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(contextSection))
            next[ContextSection] = contextSection;
        long ts = NextTimestamp(checkpoint.Timestamp);
        return new TranscriptState(next, SectionOrder(next), ts, BuildSystemText(next));
    }

    /// <summary>测试清理：丢弃会话级快照。</summary>
    public static void ClearForTests() => SessionStates.Clear();

    /// <summary>
    /// context 段构造器：变量（路径、mode、日期、水位）一律放段尾。
    /// runtime 头部禁止出现时间戳/token 数（反模式）。
    /// </summary>
    public static string BuildContextSection(
        string? body,
        string? workspacePath = null,
        string? mode = null,
        string? date = null,
        string? waterLevel = null
    )
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(body))
            lines.Add(body.TrimEnd());
        var tail = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspacePath))
            tail.Add("- workspace: " + workspacePath.Trim());
        if (!string.IsNullOrWhiteSpace(mode))
            tail.Add("- mode: " + mode.Trim());
        if (!string.IsNullOrWhiteSpace(date))
            tail.Add("- date: " + date.Trim());
        if (!string.IsNullOrWhiteSpace(waterLevel))
            tail.Add("- budget: " + waterLevel.Trim());
        if (tail.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.AddRange(tail);
        }
        return string.Join("\n", lines);
    }

    private static bool SectionsEqual(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b
    )
    {
        var an = a.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        var bn = b.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        if (an.Count != bn.Count)
            return false;
        foreach (var kv in an)
        {
            if (!b.TryGetValue(kv.Key, out string? v))
                return false;
            if (
                !string.Equals(
                    (kv.Value ?? string.Empty).TrimEnd(),
                    (v ?? string.Empty).TrimEnd(),
                    StringComparison.Ordinal
                )
            )
                return false;
        }
        return true;
    }

    private static long NextTimestamp(long prev)
    {
        long floor = Interlocked.Increment(ref s_floor);
        long next = Math.Max(prev + 1, floor);
        // 若 floor 落后（prev 更大），一次性追平，保证全局单调。
        long cur = Interlocked.Read(ref s_floor);
        while (next > cur)
        {
            long exchanged = Interlocked.CompareExchange(ref s_floor, next, cur);
            if (exchanged == cur)
                break;
            cur = exchanged;
        }
        return next;
    }
}
