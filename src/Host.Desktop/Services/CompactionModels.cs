using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>need05-03：上下文压缩结果。</summary>
public sealed class CompactionResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int RemovedMessages { get; init; }
    public int KeptMessages { get; init; }

    /// <summary>摘要首 200 字，事件展示用。</summary>
    public string SummaryPreview { get; init; } = string.Empty;
    public string? BackupFileName { get; init; }

    /// <summary>P007：桩消息 id（stub 模式成功时非空）。</summary>
    public string? StubMessageId { get; init; }

    /// <summary>P007：桩轮 id（stub 模式成功时非空，v1 会话为 null）。</summary>
    public string? StubTurnId { get; init; }

    /// <summary>P007：压缩请求 completion tokens（= meta_json.summaryTokens）。</summary>
    public int SummaryOutputTokens { get; init; }

    public static CompactionResult Ok(int removed, int kept, string preview, string? backup) =>
        new()
        {
            Success = true,
            RemovedMessages = removed,
            KeptMessages = kept,
            SummaryPreview = preview ?? string.Empty,
            BackupFileName = backup
        };

    /// <summary>P007：append-only 桩写入成功（不删旧行；Kept = 全量条数）。</summary>
    public static CompactionResult OkStub(
        int removed,
        int kept,
        string stubMsgId,
        string? stubTurnId,
        int summaryTokens,
        string preview,
        string? backupFile = null
    ) =>
        new()
        {
            Success = true,
            RemovedMessages = removed,
            KeptMessages = kept,
            SummaryPreview = preview ?? string.Empty,
            BackupFileName = backupFile,
            StubMessageId = stubMsgId,
            StubTurnId = stubTurnId,
            SummaryOutputTokens = summaryTokens,
        };

    /// <summary>无事可做（太短无需压缩）：Success 且 Removed==0，前端据此提示。</summary>
    public static CompactionResult NoOp() => new() { Success = true };

    public static CompactionResult Fail(string code, string message) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
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
