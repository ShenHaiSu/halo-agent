namespace Host.Desktop.Tools;

/// <summary>
/// 工具执行结果信封。
/// </summary>
public sealed class ToolResult
{
    public ToolResult()
    {
    }

    /// <summary>是否执行成功。</summary>
    public bool Success { get; set; }

    /// <summary>返回给 LLM / 前端的正文（已过 ToolOutputGuard）。</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>失败时的诊断信息（含自愈提示），成功时为空。</summary>
    public string? Error { get; set; }

    /// <summary>进程退出码（非进程型工具成功为 0、失败为 -1）。</summary>
    public int ExitCode { get; set; }

    /// <summary>执行耗时毫秒。</summary>
    public long DurationMs { get; set; }

    /// <summary>超长输出落盘文件绝对路径（未超限为 null）。</summary>
    public string? SpilledFile { get; set; }

    public static ToolResult Ok(
        string output,
        int exitCode = 0,
        long durationMs = 0,
        string? spilledFile = null) => new()
        {
            Success = true,
            Output = output ?? string.Empty,
            ExitCode = exitCode,
            DurationMs = durationMs,
            SpilledFile = spilledFile,
        };

    public static ToolResult Fail(
        string error,
        string? output = null,
        int exitCode = -1,
        long durationMs = 0,
        string? spilledFile = null) => new()
        {
            Success = false,
            Output = output ?? string.Empty,
            Error = error ?? "未知工具执行错误",
            ExitCode = exitCode,
            DurationMs = durationMs,
            SpilledFile = spilledFile,
        };
}
