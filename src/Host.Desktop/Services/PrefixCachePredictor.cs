using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// need05-06：本地 Prompt Cache 命中预测 + 分块拼接对比。
/// 口径：实际值永远以后端 usage 为准；本地只算预期值（predicted~），用于发送前 / 无 usage 时展示。
/// 分块顺序对齐 need05-02 稳定前缀装配线：
/// [0]system.stable → [1]historyPrefix(除末条) → [2]latest(末条) → [3]system.ephemeral(日期尾部) → [4]tools(按name升序)。
/// </summary>
public static class PrefixCachePredictor
{
    public const string EphemeralMarker = "## 4. Session Ephemeral";
    public const double EwmaAlpha = 0.3;

    public sealed record PrefixBlock(string Name, string Hash, int EstimatedTokens);
    public sealed record PrefixSnapshot(
        string PrefixHash,
        IReadOnlyList<PrefixBlock> Blocks,
        IReadOnlyList<string> HistoryMessageHashes,
        IReadOnlyList<int> HistoryMessageTokens,
        int TotalEst);
    public sealed record PrefixDiff(
        bool IsFirst,
        bool IsStable,
        int DivergenceIndex,
        string DivergenceBlock,
        int StableTokens,
        int ExpectedCachedTokens,
        double? ExpectedHitRate,
        int LcpMessages);

    private sealed record SessionPrediction(
        PrefixSnapshot Snapshot,
        double? LastActualHitRate,
        int LastActualCached,
        double? LastPredictedHitRate,
        int LastPredictedCached,
        string DivergenceBlock);

    private static readonly ConcurrentDictionary<string, SessionPrediction> Store = new(StringComparer.Ordinal);

    public static (string stable, string ephemeral) SplitSystem(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return (string.Empty, string.Empty);
        int idx = prompt.IndexOf(EphemeralMarker, StringComparison.Ordinal);
        if (idx < 0) return (prompt, string.Empty);
        return (prompt[..idx], prompt[idx..]);
    }

    public static string CanonicalMessageHash(UnifiedChatMessage m)
    {
        string toolPart = string.Empty;
        if (m.ToolCalls != null && m.ToolCalls.Count > 0)
        {
            toolPart = string.Join(";", m.ToolCalls.Select(c =>
                $"{c.CallId}\u0001{c.FunctionName}\u0001{c.ArgumentsJson}"));
        }
        string canonical = $"{m.Role ?? string.Empty}\n{m.Content ?? string.Empty}\n{m.ReasoningContent ?? string.Empty}\n{m.Name ?? string.Empty}\n{m.ToolCallId ?? string.Empty}\n{toolPart}";
        return HeaderVariableResolver.ComputeDeterministicHash16(canonical);
    }

    public static string CanonicalToolsHash(IReadOnlyList<UnifiedToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0)
            return HeaderVariableResolver.ComputeDeterministicHash16(string.Empty);
        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var parts = ordered.Select(t =>
            $"{t.Name ?? string.Empty}\u0001{t.Description ?? string.Empty}\u0001{t.ParametersSchema.GetRawText()}");
        return HeaderVariableResolver.ComputeDeterministicHash16(string.Join("\u0002", parts));
    }

    /// <summary>按 5 块装配快照；historyPrefix 保留逐消息哈希以支持块内 LCP。</summary>
    public static PrefixSnapshot BuildSnapshot(
        string? systemPrompt,
        IReadOnlyList<UnifiedChatMessage>? messages,
        IReadOnlyList<UnifiedToolDefinition>? tools)
    {
        var (stable, ephemeral) = SplitSystem(systemPrompt);
        var msgs = messages ?? Array.Empty<UnifiedChatMessage>();

        List<string> histHashes = new();
        List<int> histTokens = new();
        int historyPrefixTokens = 0;
        if (msgs.Count > 1)
        {
            for (int i = 0; i < msgs.Count - 1; i++)
            {
                var m = msgs[i];
                if (m == null) continue;
                histHashes.Add(CanonicalMessageHash(m));
                int tk = TokenBudgetService.EstimateMessageTokens(m);
                histTokens.Add(tk);
                historyPrefixTokens += tk;
            }
        }

        int latestTokens = 0;
        string latestHash = HeaderVariableResolver.ComputeDeterministicHash16(string.Empty);
        if (msgs.Count > 0 && msgs[^1] != null)
        {
            latestHash = CanonicalMessageHash(msgs[^1]!);
            latestTokens = TokenBudgetService.EstimateMessageTokens(msgs[^1]!);
        }

        int stableTokens = TokenBudgetService.EstimateTokens(stable);
        int ephemeralTokens = string.IsNullOrEmpty(ephemeral) ? 0 : TokenBudgetService.EstimateTokens(ephemeral);
        string toolsHash = CanonicalToolsHash(tools);
        int toolsTokens = 0;
        if (tools != null)
        {
            foreach (var t in tools)
                toolsTokens += TokenBudgetService.EstimateTokens(t.Name) + TokenBudgetService.EstimateTokens(t.Description);
        }

        var blocks = new List<PrefixBlock>
        {
            new("system.stable", HeaderVariableResolver.ComputeDeterministicHash16(stable ?? string.Empty), stableTokens),
            new("historyPrefix", HeaderVariableResolver.ComputeDeterministicHash16(string.Join("|", histHashes)), historyPrefixTokens),
            new("latest", latestHash, latestTokens),
            new("system.ephemeral", HeaderVariableResolver.ComputeDeterministicHash16(ephemeral ?? string.Empty), ephemeralTokens),
            new("tools", toolsHash, toolsTokens),
        };
        int total = blocks.Sum(b => b.EstimatedTokens);
        string prefixHash = HeaderVariableResolver.ComputeDeterministicHash16(string.Join("|", blocks.Select(b => b.Hash)));
        return new PrefixSnapshot(prefixHash, blocks, histHashes, histTokens, total);
    }

    /// <summary>
    /// 拼接对比：找首个哈希不同的块；若断裂在 historyPrefix，做消息级 LCP，稳定 tokens = 前导整块 + LCP 消息。
    /// 全等 → IsStable；prev==null → IsFirst，ExpectedHitRate=null（无依据不瞎猜）。
    /// </summary>
    public static PrefixDiff Compare(PrefixSnapshot? prev, PrefixSnapshot cur, double? lastActualHitRate = null)
    {
        if (prev == null) return new PrefixDiff(true, false, -1, "-", 0, 0, null, 0);

        int div = -1;
        int n = Math.Min(prev.Blocks.Count, cur.Blocks.Count);
        for (int i = 0; i < n; i++)
        {
            if (!string.Equals(prev.Blocks[i].Hash, cur.Blocks[i].Hash, StringComparison.Ordinal))
            {
                div = i;
                break;
            }
        }
        if (div < 0 && prev.Blocks.Count != cur.Blocks.Count) div = n;
        if (div < 0)
        {
            // 全等：预期沿用上轮实测（持平语义），无实测则 100%。
            double hit = lastActualHitRate ?? 1.0;
            hit = Math.Clamp(hit, 0.0, 1.0);
            int cached = lastActualHitRate.HasValue
                ? Math.Min(cur.TotalEst, (int)Math.Round(cur.TotalEst * hit))
                : cur.TotalEst;
            return new PrefixDiff(false, true, -1, "stable", cur.TotalEst, cached, hit, cur.HistoryMessageHashes.Count);
        }

        int stableTokens = 0;
        for (int i = 0; i < div; i++) stableTokens += cur.Blocks[i].EstimatedTokens;
        int lcpMessages = 0;

        // 断裂在 historyPrefix(1)：消息级 LCP，避免整块归零。
        if (div == 1)
        {
            int m = Math.Min(prev.HistoryMessageHashes.Count, cur.HistoryMessageHashes.Count);
            for (int i = 0; i < m; i++)
            {
                if (string.Equals(prev.HistoryMessageHashes[i], cur.HistoryMessageHashes[i], StringComparison.Ordinal))
                {
                    lcpMessages++;
                    stableTokens += cur.HistoryMessageTokens[i];
                }
                else break;
            }
        }

        double? rate = cur.TotalEst > 0 ? Math.Clamp((double)stableTokens / cur.TotalEst, 0.0, 1.0) : null;
        return new PrefixDiff(false, false, div, cur.Blocks[div].Name, stableTokens, stableTokens, rate, lcpMessages);
    }

    /// <summary>字节级 LCP（用于 payload 拼接对比取证），O(min(n,m))。</summary>
    public static int ComputeByteLcpLength(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    public static double SmoothEwma(double? prev, double sample, double alpha = EwmaAlpha)
    {
        if (!prev.HasValue) return sample;
        return alpha * sample + (1 - alpha) * prev.Value;
    }

    private static string StoreKey(string? sessionHash, string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionHash) ? sessionHash!.Trim()
            : !string.IsNullOrWhiteSpace(sessionId) ? "sid:" + sessionId!.Trim() : string.Empty;

    /// <summary>网关发送前调用：计算预测并落存储；返回 (predictedHit, predictedCached, divergenceBlock)。</summary>
    public static (double? predictedHit, int predictedCached, string divergenceBlock) TrackRequest(
        string? sessionHash, string? sessionId, PrefixSnapshot cur)
    {
        string key = StoreKey(sessionHash, sessionId);
        if (string.IsNullOrEmpty(key)) return (null, 0, "-");
        Store.TryGetValue(key, out var prev);
        var diff = Compare(prev?.Snapshot, cur, prev?.LastActualHitRate);
        double? raw = diff.ExpectedHitRate;
        double? smoothed = null;
        if (raw.HasValue)
            smoothed = prev?.LastPredictedHitRate.HasValue == true && !diff.IsFirst
                ? SmoothEwma(prev!.LastPredictedHitRate, raw.Value)
                : raw.Value;
        var next = new SessionPrediction(cur, prev?.LastActualHitRate, prev?.LastActualCached ?? 0,
            smoothed, diff.ExpectedCachedTokens, diff.DivergenceBlock);
        Store[key] = next;
        return (smoothed, diff.ExpectedCachedTokens, diff.DivergenceBlock);
    }

    /// <summary>网关收到 usage 后调用：记录实测，用于下轮全等时持平预测。</summary>
    public static void RecordActual(string? sessionHash, string? sessionId, int inputTokens, int cachedTokens)
    {
        string key = StoreKey(sessionHash, sessionId);
        if (string.IsNullOrEmpty(key)) return;
        double? hit = inputTokens > 0 ? Math.Clamp((double)cachedTokens / inputTokens, 0.0, 1.0) : null;
        if (Store.TryGetValue(key, out var prev))
            Store[key] = prev with { LastActualHitRate = hit, LastActualCached = Math.Min(cachedTokens, Math.Max(0, inputTokens)) };
        // 无 TrackRequest 先行（如历史消息回放）则不建条目，避免污染预测。
    }

     public static bool TryGetPrediction(string? sessionHash, out double? hitRate, out int cachedTokens, out string divergenceBlock)
     {
         hitRate = null; cachedTokens = 0; divergenceBlock = "-";
         if (string.IsNullOrWhiteSpace(sessionHash)) return false;
         if (Store.TryGetValue(sessionHash.Trim(), out var p) && p.LastPredictedHitRate.HasValue)
         {
             hitRate = p.LastPredictedHitRate;
             cachedTokens = p.LastPredictedCached;
             divergenceBlock = p.DivergenceBlock;
             return true;
         }
         return false;
     }
 
     /// <summary>测试清理。</summary>
     public static void ClearForTests() => Store.Clear();
 }
