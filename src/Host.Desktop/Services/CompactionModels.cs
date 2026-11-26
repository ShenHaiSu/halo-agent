using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

public enum CompactionOutcome
{
    Completed,
    NoOp,
    Failed
}

public static class CompactionNoOpReasons
{
    public const string Empty = "EMPTY";
    public const string TooShort = "TOO_SHORT";
    public const string NoNewContent = "NO_NEW_CONTENT";
}

/// <summary>上下文压缩结果。Success 保留兼容语义，新逻辑必须优先判断 Outcome。</summary>
public sealed class CompactionResult
{
    public CompactionOutcome Outcome { get; init; }
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? NoOpReason { get; init; }
    public string? MetadataWarning { get; init; }
    public int RemovedMessages { get; init; }
    public int KeptMessages { get; init; }

    /// <summary>摘要首 200 字，事件展示用。</summary>
    public string SummaryPreview { get; init; } = string.Empty;
    public string? BackupFileName { get; init; }
    public string? StubMessageId { get; init; }
    public string? StubTurnId { get; init; }

    /// <summary>最终写入桩的摘要 token；兼容旧事件字段。</summary>
    public int SummaryOutputTokens { get; init; }

    /// <summary>本次 compact 的 map/reduce 请求总数。</summary>
    public int CompactLlmCallCount { get; init; }

    public static CompactionResult Ok(
        int removed,
        int kept,
        string preview,
        string? backup,
        int summaryTokens = 0,
        int compactLlmCallCount = 0
    ) =>
        new()
        {
            Outcome = CompactionOutcome.Completed,
            Success = true,
            RemovedMessages = removed,
            KeptMessages = kept,
            SummaryPreview = preview ?? string.Empty,
            BackupFileName = backup,
            SummaryOutputTokens = summaryTokens,
            CompactLlmCallCount = compactLlmCallCount,
        };

    public static CompactionResult OkStub(
        int removed,
        int kept,
        string stubMsgId,
        string? stubTurnId,
        int summaryTokens,
        string preview,
        string? backupFile = null,
        string? metadataWarning = null,
        int compactLlmCallCount = 0
    ) =>
        new()
        {
            Outcome = CompactionOutcome.Completed,
            Success = true,
            RemovedMessages = removed,
            KeptMessages = kept,
            SummaryPreview = preview ?? string.Empty,
            BackupFileName = backupFile,
            StubMessageId = stubMsgId,
            StubTurnId = stubTurnId,
            SummaryOutputTokens = summaryTokens,
            MetadataWarning = metadataWarning,
            CompactLlmCallCount = compactLlmCallCount,
        };

    public static CompactionResult NoOp(string? reason = null) =>
        new()
        {
            Outcome = CompactionOutcome.NoOp,
            Success = true,
            NoOpReason = reason,
        };

    public static CompactionResult Fail(string code, string message) =>
        new()
        {
            Outcome = CompactionOutcome.Failed,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
        };
}

public interface ICompactionService
{
    Task<CompactionResult> CompactAsync(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6,
        CancellationToken ct = default
    );
}
