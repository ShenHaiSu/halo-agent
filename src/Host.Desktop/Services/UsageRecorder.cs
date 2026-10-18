using System;
using System.Collections.Generic;
using System.Text;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 用量补记小帮手（need12-P1/P2）：所有调用方统一经此落袋，保证永不抛、幂等可丢。
/// Source 口径：agent | session | compact | title。Estimated=1 表示本地估算（无网关实测时）。
/// </summary>
public static class UsageRecorder
{
    public const string SourceAgent = "agent";
    public const string SourceSession = "session";
    public const string SourceCompact = "compact";
    public const string SourceTitle = "title";

    public static bool IsValidSource(string? source) =>
        string.Equals(source, SourceAgent, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, SourceSession, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, SourceCompact, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, SourceTitle, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeSource(string? source, string fallback = SourceAgent)
    {
        if (IsValidSource(source)) return source!.Trim().ToLowerInvariant();
        return fallback;
    }

    /// <summary>实测值落袋（Estimated 恒 0）。</summary>
    public static void Collect(
        List<PendingUsageRow> rows,
        ProviderConfig? provider,
        ModelConfig? model,
        UnifiedTokenUsage? usage,
        string status,
        string requestId,
        string sessionId,
        string workspaceId,
        string source = SourceAgent)
    {
        try
        {
            if (rows == null || provider == null || model == null) return;
            rows.Add(new PendingUsageRow
            {
                ProviderId = provider.Id ?? string.Empty,
                ProviderName = provider.Name ?? provider.Id ?? string.Empty,
                ModelId = model.Id ?? string.Empty,
                ModelDisplay = string.IsNullOrWhiteSpace(model.DisplayName) ? (model.Id ?? string.Empty) : model.DisplayName,
                Protocol = model.Protocol == LlmProtocolType.Resp ? "resp" : "chat",
                Input = Math.Max(0, usage?.InputTokens ?? 0),
                Output = Math.Max(0, usage?.OutputTokens ?? 0),
                Cached = Math.Max(0, usage?.CachedInputTokens ?? 0),
                Status = string.IsNullOrWhiteSpace(status) ? "ok" : status.Trim(),
                RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
                SessionId = sessionId ?? string.Empty,
                WorkspaceId = workspaceId ?? string.Empty,
                Source = NormalizeSource(source),
                Estimated = 0,
            });
        }
        catch { /* 永不阻塞主链路 */ }
    }

    /// <summary>
    /// 估算值落袋（仅当 usage==null 且请求已发出时调用；Cached 恒 0，Estimated 恒 1）。
    /// 即使全 0 也保留一行，保证请求计数不丢。
    /// </summary>
    public static void CollectEstimated(
        List<PendingUsageRow> rows,
        ProviderConfig? provider,
        ModelConfig? model,
        int estimatedInput,
        int estimatedOutput,
        string status,
        string requestId,
        string sessionId,
        string workspaceId,
        string source = SourceAgent)
    {
        try
        {
            if (rows == null || provider == null || model == null) return;
            rows.Add(new PendingUsageRow
            {
                ProviderId = provider.Id ?? string.Empty,
                ProviderName = provider.Name ?? provider.Id ?? string.Empty,
                ModelId = model.Id ?? string.Empty,
                ModelDisplay = string.IsNullOrWhiteSpace(model.DisplayName) ? (model.Id ?? string.Empty) : model.DisplayName,
                Protocol = model.Protocol == LlmProtocolType.Resp ? "resp" : "chat",
                Input = Math.Max(0, estimatedInput),
                Output = Math.Max(0, estimatedOutput),
                Cached = 0,
                Status = string.IsNullOrWhiteSpace(status) ? "cancelled" : status.Trim(),
                RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
                SessionId = sessionId ?? string.Empty,
                WorkspaceId = workspaceId ?? string.Empty,
                Source = NormalizeSource(source),
                Estimated = 1,
            });
        }
        catch { /* 永不阻塞主链路 */ }
    }

    /// <summary>智能落袋：有实测用实测，无实测用估算。</summary>
    public static void CollectAuto(
        List<PendingUsageRow> rows,
        ProviderConfig? provider,
        ModelConfig? model,
        UnifiedTokenUsage? usage,
        int estimatedInput,
        int estimatedOutput,
        string status,
        string requestId,
        string sessionId,
        string workspaceId,
        string source = SourceAgent)
    {
        try
        {
            if (usage != null)
                Collect(rows, provider, model, usage, status, requestId, sessionId, workspaceId, source);
            else
                CollectEstimated(rows, provider, model, estimatedInput, estimatedOutput, status, requestId, sessionId, workspaceId, source);
        }
        catch { /* 永不阻塞主链路 */ }
    }

    public static void Flush(UsageMetricsService? svc, List<PendingUsageRow> rows, string sessionId, string workspaceId)
    {
        try { svc?.EnqueueFlush(rows, sessionId, workspaceId); } catch { /* 永不抛 */ }
    }

    /// <summary>输入估算 = system + 全量 messages 启发式（含图片开销与 tool 结构开销）。失败回退 0。</summary>
    public static int EstimateInput(IReadOnlyList<UnifiedChatMessage>? messages, string? systemPrompt = null)
    {
        try
        {
            int total = 0;
            if (!string.IsNullOrEmpty(systemPrompt)) total += TokenBudgetService.EstimateTokens(systemPrompt);
            if (messages != null)
                foreach (var m in messages)
                    total += TokenBudgetService.EstimateMessageTokens(m);
            return Math.Max(0, total);
        }
        catch { return 0; }
    }

    /// <summary>输入估算（纯文本体，用于 compact body / title prompt）。</summary>
    public static int EstimateInputText(string? text)
    {
        try { return Math.Max(0, TokenBudgetService.EstimateTokens(text)); }
        catch { return 0; }
    }

    /// <summary>输出估算 = 已产出文本（含 reasoning）启发式。失败回退 0。</summary>
    public static int EstimateOutput(string? text, string? reasoning = null)
    {
        try
        {
            int total = TokenBudgetService.EstimateTokens(text);
            if (!string.IsNullOrEmpty(reasoning)) total += TokenBudgetService.EstimateTokens(reasoning);
            return Math.Max(0, total);
        }
        catch { return 0; }
    }

    public static int EstimateOutput(StringBuilder? textAcc, StringBuilder? reasoningAcc)
    {
        try
        {
            string? t = textAcc?.Length > 0 ? textAcc.ToString() : null;
            string? r = reasoningAcc?.Length > 0 ? reasoningAcc.ToString() : null;
            return EstimateOutput(t, r);
        }
        catch { return 0; }
    }
}
