using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>把完整活跃历史归一化为原子摘要单元，并在模型预算内顺序分片。</summary>
internal sealed class CompactionSummaryPlanner
{
    private const int SummaryPromptReserveTokens = 512;
    private const int DefaultMessageCharLimit = 2000;
    private readonly ITokenBudgetService _budgetService;

    public CompactionSummaryPlanner(ITokenBudgetService budgetService)
    {
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
    }

    public CompactionSummaryPlan Plan(
        IReadOnlyList<UnifiedChatMessage>? messages,
        string? previousSummary,
        ModelConfig model,
        CancellationToken ct = default
    )
    {
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        int maxOutputTokens = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        int inputLimit = CompactionSummaryInput.ComputeInputLimit(
            ContextBudgetLimits.FromModel(model),
            maxOutputTokens
        );
        int available = Math.Max(1, inputLimit - SummaryPromptReserveTokens);
        int chunkLimit = Math.Max(1, (int)(available * 0.9));

        int lead = 0;
        while (
            lead < messages.Count
            && string.Equals(messages[lead]?.Role, "system", StringComparison.OrdinalIgnoreCase)
        )
            lead++;

        var candidates = new List<(UnifiedChatMessage Original, UnifiedChatMessage Reduced)>();
        var coveredIds = new List<string>();
        int excluded = 0;
        for (int i = lead; i < messages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var original = messages[i];
            if (original == null)
            {
                excluded++;
                continue;
            }
            coveredIds.Add(original.Id ?? string.Empty);
            if (CompactionSummaryInput.IsDropped(original) || IsEmptyAssistant(original))
            {
                excluded++;
                continue;
            }
            var reduced = CompactionSummaryInput.ReduceSummaryInput(
                new[] { original },
                CompactionPlan.ToolResultReduceChars
            )[0];
            candidates.Add((original, reduced));
        }

        var units = new List<CompactionSummaryUnit>();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            string prefixText = "[上轮上下文摘要]\n" + previousSummary.Trim();
            int prefixTokens = _budgetService.EstimateTokens(prefixText);
            if (prefixTokens > chunkLimit)
                throw new CompactionSummaryException(
                    "COMPACT_SUMMARY_UNIT_TOO_LARGE",
                    "上一轮摘要超过本次摘要输入预算"
                );
            units.Add(
                new CompactionSummaryUnit(
                    units.Count + 1,
                    prefixText,
                    prefixTokens,
                    Array.Empty<string>(),
                    false
                )
            );
        }

        var groups = _budgetService.GroupIntoAtomicTurns(
            candidates.Select(candidate => candidate.Reduced).ToList()
        );
        var originalsById = candidates.ToDictionary(
            candidate => candidate.Original.Id ?? string.Empty,
            candidate => candidate.Original,
            StringComparer.Ordinal
        );
        int truncated = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var originals = group
                .Messages.Select(message => originalsById[message.Id ?? string.Empty])
                .ToList();
            var unit = CreateFittedUnit(group.Messages, originals, units.Count + 1, chunkLimit);
            truncated += unit.TruncatedMessageCount;
            units.Add(unit);
        }

        if (units.Count == 0)
            throw new CompactionSummaryException("COMPACT_SUMMARY_EMPTY", "没有可进入摘要模型的历史内容");

        var chunks = new List<CompactionSummaryChunk>();
        var currentUnits = new List<CompactionSummaryUnit>();
        int currentTokens = 0;
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            int separatorTokens = currentUnits.Count > 0 ? 1 : 0;
            if (
                currentUnits.Count > 0
                && currentTokens + separatorTokens + unit.EstimatedTokens > chunkLimit
            )
            {
                chunks.Add(CreateChunk(chunks.Count + 1, currentUnits, currentTokens));
                currentUnits = new List<CompactionSummaryUnit>();
                currentTokens = 0;
                separatorTokens = 0;
            }
            currentUnits.Add(unit);
            currentTokens += separatorTokens + unit.EstimatedTokens;
        }
        if (currentUnits.Count > 0)
            chunks.Add(CreateChunk(chunks.Count + 1, currentUnits, currentTokens));
        if (chunks.Count > CompactionPlan.MaxSummaryChunks)
            throw new CompactionSummaryException(
                "COMPACT_SUMMARY_TOO_LARGE",
                "完整历史超过摘要分片上限，未写入压缩桩"
            );
        chunks = chunks.Select(chunk => chunk with { Total = chunks.Count }).ToList();

        int totalEstimated = chunks.Sum(chunk => chunk.EstimatedTokens);
        var coverage = new CompactionSummaryCoverage(
            coveredIds.Count,
            candidates.Count,
            excluded,
            truncated,
            coveredIds
        );
        return new CompactionSummaryPlan(
            chunks,
            inputLimit,
            chunkLimit,
            totalEstimated,
            chunks.Count > 1,
            coverage
        );
    }

    private CompactionSummaryUnit CreateFittedUnit(
        IReadOnlyList<UnifiedChatMessage> reducedMessages,
        IReadOnlyList<UnifiedChatMessage> originalMessages,
        int index,
        int chunkLimit
    )
    {
        int charLimit = DefaultMessageCharLimit;
        while (true)
        {
            string text = BuildUnitText(reducedMessages, charLimit, out bool truncated);
            int tokens = _budgetService.EstimateTokens(text);
            if (tokens <= chunkLimit)
            {
                int truncatedMessages = originalMessages.Count(message =>
                    WasMessageTruncated(message, charLimit)
                );
                return new CompactionSummaryUnit(
                    index,
                    text,
                    tokens,
                    originalMessages.Select(message => message.Id ?? string.Empty).ToArray(),
                    truncated || truncatedMessages > 0,
                    truncatedMessages
                );
            }
            if (charLimit <= 1)
                throw new CompactionSummaryException(
                    "COMPACT_SUMMARY_UNIT_TOO_LARGE",
                    "单个原子摘要组无法安全放入输入预算"
                );
            charLimit = Math.Max(1, charLimit / 2);
        }
    }

    private static bool WasMessageTruncated(UnifiedChatMessage message, int charLimit)
    {
        if ((message.Content?.Length ?? 0) > charLimit)
            return true;
        if (
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && (message.Content?.Length ?? 0) > CompactionPlan.ToolResultReduceChars
        )
            return true;
        return message.ToolCalls?.Any(call => (call.ArgumentsJson?.Length ?? 0) > charLimit)
            ?? false;
    }

    private static CompactionSummaryChunk CreateChunk(
        int index,
        IReadOnlyList<CompactionSummaryUnit> units,
        int estimatedTokens
    ) => new(index, 0, units.ToArray(), estimatedTokens);

    private static string BuildUnitText(
        IReadOnlyList<UnifiedChatMessage> messages,
        int charLimit,
        out bool truncated
    )
    {
        var builder = new System.Text.StringBuilder();
        truncated = false;
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (i > 0)
                builder.Append('\n');
            builder.Append('[').Append(RoleLabel(message.Role)).Append("] ");
            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                foreach (var call in message.ToolCalls)
                {
                    string value = call.FunctionName + ":" + (call.ArgumentsJson ?? string.Empty);
                    builder.Append(Truncate(value, charLimit, ref truncated)).Append(' ');
                }
            }
            string content = message.Content ?? string.Empty;
            builder.Append(Truncate(content, charLimit, ref truncated));
        }
        if (truncated)
            builder.Append("\n[部分消息为满足预算已截断]");
        return builder.ToString();
    }

    private static string Truncate(string value, int limit, ref bool truncated)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= limit)
            return value;
        truncated = true;
        return value.Substring(0, Math.Max(1, limit)) + "…[truncated]";
    }

    private static bool IsEmptyAssistant(UnifiedChatMessage message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(message.Content)
        && string.IsNullOrWhiteSpace(message.ReasoningContent)
        && (message.ToolCalls == null || message.ToolCalls.Count == 0);

    private static string RoleLabel(string? role) =>
        role?.ToLowerInvariant() switch
        {
            "user" => "用户",
            "assistant" => "助手",
            "tool" => "工具结果",
            "system" => "系统",
            _ => role ?? "未知",
        };
}
