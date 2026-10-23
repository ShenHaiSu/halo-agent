using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-3：摘要输入（有预算、有降级、有分片）。
/// estimateSummaryTokens = max(history + prevSummary, turnPrefix)/3.5；
/// inputLimit = hard + headroom - min(0.8*headroom, maxTokens) - 2048。
/// 超限先 ReduceSummaryInput（toolResult→500ch+后缀，去 thinking），再 PlanSummaryChunks（×0.9 margin，≤16 块，单消息截断）；
/// 顺序：systemJson → compactionSummary → retainedReasoning → retainedTail，
/// 过滤 error/aborted/deferred/空 assistant。
/// </summary>
public static class CompactionSummaryInput
{
    /// <summary>摘要 LLM 调用重试间隔：2s/4s/8s（共 3 次重试）。</summary>
    public static readonly int[] RetryDelaysMs = { 2000, 4000, 8000 };

    public const string DroppedStatusPrefixError = "[error]";
    public const string DroppedStatusPrefixAborted = "[aborted]";
    public const string DroppedStatusPrefixDeferred = "[deferred]";

    /// <summary>estimateSummaryTokens = max(history + prevSummary, turnPrefix)/3.5。</summary>
    public static int EstimateSummaryTokens(
        string? historyText,
        string? prevSummary,
        string? turnPrefix
    )
    {
        long history = (historyText?.Length ?? 0) + (prevSummary?.Length ?? 0);
        long prefix = turnPrefix?.Length ?? 0;
        return (int)Math.Max(1, Math.Ceiling(Math.Max(history, prefix) / 3.5));
    }

    /// <summary>inputLimit = hard + headroom - min(0.8*headroom, maxTokens) - 2048。</summary>
    public static int ComputeInputLimit(ContextBudgetLimits limits, int maxTokens)
    {
        if (limits == null)
            throw new ArgumentNullException(nameof(limits));
        int cap = Math.Max(1, maxTokens);
        int reserve = Math.Min((int)(limits.Headroom * 0.8), cap);
        return Math.Max(1, limits.HardLimit + limits.Headroom - reserve - 2048);
    }

    /// <summary>
    /// 超限先降级：toolResult→500ch+后缀；去 thinking（ReasoningContent 清空）。
    /// 返回降级后的拷贝列表（不改输入）。
    /// </summary>
    public static List<UnifiedChatMessage> ReduceSummaryInput(
        IReadOnlyList<UnifiedChatMessage>? messages,
        int toolResultChars = CompactionPlan.ToolResultReduceChars
    )
    {
        var list = new List<UnifiedChatMessage>();
        if (messages == null)
            return list;
        foreach (var m in messages)
        {
            if (m == null)
                continue;
            var copy = new UnifiedChatMessage
            {
                Id = m.Id,
                CreatedAtUtc = m.CreatedAtUtc,
                Role = m.Role,
                Content = m.Content,
                ReasoningContent = null, // 去 thinking
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId,
                Name = m.Name,
            };
            if (
                string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && (copy.Content?.Length ?? 0) > toolResultChars
            )
                copy.Content =
                    copy.Content!.Substring(0, toolResultChars)
                    + CompactionPlan.ToolResultTruncatedSuffix;
            list.Add(copy);
        }
        return list;
    }

    /// <summary>
    /// 再分片：按字符拼装，×0.9 margin，≤16 块；单消息超限截断到尾。
    /// 返回每块文本。
    /// </summary>
    public static List<string> PlanSummaryChunks(string body, int inputLimit)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(body))
            return chunks;
        int budget = Math.Max(64, (int)(inputLimit * 0.9));
        if (body.Length <= budget)
        {
            chunks.Add(body);
            return chunks;
        }
        // 按行贪心拼装，单行超限则硬截断到尾；超 MaxSummaryChunks 时合并最旧块（保尾）。
        var lines = body.Split('\n');
        var cur = new StringBuilder();
        foreach (string rawLine in lines)
        {
            string line = rawLine;
            if (line.Length > budget)
                line = "…" + line.Substring(line.Length - budget);
            if (cur.Length + line.Length + 1 > budget && cur.Length > 0)
            {
                chunks.Add(cur.ToString());
                cur.Clear();
            }
            if (cur.Length > 0)
                cur.Append('\n');
            cur.Append(line);
            if (chunks.Count >= CompactionPlan.MaxSummaryChunks - 1 && cur.Length >= budget)
            {
                chunks.Add(cur.ToString());
                cur.Clear();
            }
        }
        if (cur.Length > 0)
            chunks.Add(cur.ToString());
        while (chunks.Count > CompactionPlan.MaxSummaryChunks)
        {
            // 合并最旧两块（保尾部信息）。
            string merged = chunks[0] + "\n" + chunks[1];
            if (merged.Length > budget)
                merged = "…" + merged.Substring(merged.Length - budget);
            chunks.RemoveRange(0, 2);
            chunks.Insert(0, merged);
        }
        return chunks;
    }

    /// <summary>
    /// 摘要请求消息顺序：compactionSummary（上轮摘要）→ retainedReasoning → retainedTail；
    /// 过滤 error/aborted/deferred/空 assistant。systemJson 由调用方作 SystemInstructions。
    /// </summary>
    public static List<UnifiedChatMessage> BuildInputMessages(
        string? prevSummary,
        IReadOnlyList<UnifiedChatMessage>? oldRegion,
        string? turnPrefix = null
    )
    {
        var result = new List<UnifiedChatMessage>();
        if (!string.IsNullOrWhiteSpace(prevSummary))
            result.Add(UnifiedChatMessage.User("【上轮上下文摘要】\n" + prevSummary.Trim()));
        if (!string.IsNullOrWhiteSpace(turnPrefix))
            result.Add(UnifiedChatMessage.User(turnPrefix));
        if (oldRegion == null)
            return result;
        foreach (var m in oldRegion)
        {
            if (m == null)
                continue;
            if (IsDropped(m))
                continue;
            if (
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(m.Content)
                && string.IsNullOrWhiteSpace(m.ReasoningContent)
                && (m.ToolCalls == null || m.ToolCalls.Count == 0)
            )
                continue; // 空 assistant
            result.Add(m);
        }
        return result;
    }

    /// <summary>过滤 error/aborted/deferred 状态行。</summary>
    public static bool IsDropped(UnifiedChatMessage message)
    {
        if (message == null)
            return true;
        string text = (message.Content ?? string.Empty).TrimStart();
        return text.StartsWith(DroppedStatusPrefixError, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(DroppedStatusPrefixAborted, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(DroppedStatusPrefixDeferred, StringComparison.OrdinalIgnoreCase);
    }
}
