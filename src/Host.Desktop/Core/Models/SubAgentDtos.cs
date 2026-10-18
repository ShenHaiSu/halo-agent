namespace Host.Desktop.Core.Models;

/// <summary>need16 子会话详情 DTO。</summary>
public sealed class SubSessionDetailDto
{
    public string SubId { get; set; } = string.Empty;
    public string ParentSessionId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown"; // pending|running|succeeded|failed|cancelled
    public string? TaskPrompt { get; set; }
    public string? CreatedByCallId { get; set; }
    public int MessageCount { get; set; }
    public int Iterations { get; set; }
    public int ToolExecutions { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long BilledPrompt { get; set; }
    public long BilledCompletion { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
