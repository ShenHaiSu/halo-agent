using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;

namespace Host.Desktop.Services;

internal sealed record CompactionSummaryUnit(
    int Index,
    string Text,
    int EstimatedTokens,
    IReadOnlyList<string> SourceMessageIds,
    bool Truncated,
    int TruncatedMessageCount = 0
);

internal sealed record CompactionSummaryChunk(
    int Index,
    int Total,
    IReadOnlyList<CompactionSummaryUnit> Units,
    int EstimatedTokens
);

internal sealed record CompactionSummaryCoverage(
    int CoveredMessageCount,
    int SummarizedMessageCount,
    int ExcludedMessageCount,
    int TruncatedMessageCount,
    IReadOnlyList<string> CoveredMessageIds
);

internal sealed record CompactionSummaryPlan(
    IReadOnlyList<CompactionSummaryChunk> Chunks,
    int InputTokenLimit,
    int ChunkTokenLimit,
    int TotalEstimatedTokens,
    bool RequiresMapReduce,
    CompactionSummaryCoverage Coverage
);

internal sealed class CompactionSummaryResult
{
    public string Summary { get; init; } = string.Empty;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
    public int LlmCallCount { get; init; }
    public int FinalSummaryTokens { get; init; }
    public int ChunkCount { get; init; }
    public int SummarizedMessageCount { get; init; }
    public int ExcludedMessageCount { get; init; }
    public int TruncatedMessageCount { get; init; }
}

internal sealed class CompactionSummaryException : Exception
{
    public CompactionSummaryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record CompactionSummaryExecutionContext(
    ProviderConfig Provider,
    ModelConfig Model,
    HeaderResolutionContext Header,
    string WorkspaceId,
    string SessionId
);
