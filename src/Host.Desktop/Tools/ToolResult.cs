using System.Text.RegularExpressions;

namespace Host.Desktop.Tools;

/// <summary>
/// 工具执行结果信封。
/// need15 P0-5：结构化错误码（ErrorCode + ErrorPayloadJson），老消费方无感（null 忽略）。
/// </summary>
public sealed class ToolResult
{
    private static readonly Regex CodePrefix = new(@"^([A-Z_]{2,})[:：]", RegexOptions.Compiled);

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

    /// <summary>need15 P0-5：结构化错误码（如 OLD_STRING_NOT_FOUND），成功时为 null。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    /// <summary>need15 P0-5：失配时的 nearest/hint JSON（即 BuildNotFoundDiagnostic 产物），成功时为 null。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorPayloadJson { get; set; }

    /// <summary>need17-02：中央校验 warnings（ALIASED_PARAM/DEFAULTED_PARAM/COERCED_TYPE 等，仅提示不判失败；老消费方无感）。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ValidatedWarnings { get; set; }

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
        string? spilledFile = null)
    {
        var r = new ToolResult
        {
            Success = false,
            Output = output ?? string.Empty,
            Error = error ?? "未知工具执行错误",
            ExitCode = exitCode,
            DurationMs = durationMs,
            SpilledFile = spilledFile,
        };
        r.ErrorCode = TryExtractCode(r.Error);
        return r;
    }

    /// <summary>
    /// need15 P0-5：带结构化错误码的失败工厂。P0 新增分支必须走此方法。
    /// message 可带或不带码前缀；最终 Error 首 token 必为 CODE：。
    /// </summary>
    public static ToolResult FailCode(
        string code,
        string message,
        string? payloadJson = null,
        string? output = null,
        int exitCode = -1,
        long durationMs = 0,
        string? spilledFile = null)
    {
        string msg = message ?? string.Empty;
        string prefix1 = code + "：";
        string prefix2 = code + ":";
        string error = msg.StartsWith(prefix1, StringComparison.Ordinal) || msg.StartsWith(prefix2, StringComparison.Ordinal)
            ? msg
            : $"{code}：{msg}";
        return new ToolResult
        {
            Success = false,
            Output = output ?? string.Empty,
            Error = error,
            ExitCode = exitCode,
            DurationMs = durationMs,
            SpilledFile = spilledFile,
            ErrorCode = code,
            ErrorPayloadJson = payloadJson,
        };
    }

    /// <summary>从 Error 首 token 提取错误码（全大写下划线 + 中/英文冒号）。</summary>
    public static string? TryExtractCode(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        var m = CodePrefix.Match(error);
        return m.Success ? m.Groups[1].Value : null;
    }
}
