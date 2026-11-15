using System.Text;

namespace Host.Desktop.Services;

/// <summary>
/// Single display unit under construction during streaming.
/// One contiguous run of the same kind forms one draft; the gateway sequence is only passed through.
/// </summary>
public sealed class TimelineBlockDraft
{
    public string BlockId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Epoch { get; set; } = string.Empty;
    public string Kind { get; set; } = "text";
    public string Tag { get; set; } = "pending";
    public string? CallId { get; set; }
    public string? FunctionName { get; set; }
    public int Iteration { get; set; }
    public StringBuilder Text { get; } = new();
    public StringBuilder Args { get; } = new();
}
