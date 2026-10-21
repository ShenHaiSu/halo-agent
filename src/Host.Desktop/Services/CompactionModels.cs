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

    public static CompactionResult Ok(int removed, int kept, string preview, string? backup) =>
        new()
        {
            Success = true,
            RemovedMessages = removed,
            KeptMessages = kept,
            SummaryPreview = preview ?? string.Empty,
            BackupFileName = backup
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
