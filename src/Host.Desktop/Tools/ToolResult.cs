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
     /// <summary>need12：文件写入类工具落盘字节数（WriteFileTool 显式回填，前端完成态展示用）。</summary>
     [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
     public long? FileBytes { get; set; }
     /// <summary>need09-05：工具附带的图片引用（view_image 结果；ToolOutputGuard 只截文本不动图片）。</summary>
     [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
     public List<Host.Desktop.Core.Models.ChatImageRef>? Images { get; set; }
    public static ToolResult Ok(
        string output,
        int exitCode = 0,
        long durationMs = 0,
        string? spilledFile = null,
        List<Host.Desktop.Core.Models.ChatImageRef>? images = null) => new()
        {
            Success = true,
            Output = output ?? string.Empty,
            ExitCode = exitCode,
            DurationMs = durationMs,
            SpilledFile = spilledFile,
            Images = images,
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
