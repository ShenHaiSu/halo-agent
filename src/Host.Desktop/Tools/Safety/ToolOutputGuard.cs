using System.Text;
using System.Text.RegularExpressions;

namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 超长工具输出自动截断与落盘转存守卫（Output Spillover Protection）。
/// 单次内联返回上限 16,000 字符（约 4,000 Tokens）；超限则完整输出落盘至会话 tool-outputs 目录，
/// 上下文仅保留头部 4,000 + 省略提示 + 尾部 8,000 字符（编译错误与退出状态多在尾部，尾部权重更大）。
/// need22-P0 S5：callId 白名单（非法拒绝落盘 INVALID_CALL_ID）、落盘前脱敏。
/// </summary>
public static class ToolOutputGuard
{
    /// <summary>
    /// 单次内联返回上限字符数。
    /// </summary>
    public const int MaxInlineChars = 16000;

    /// <summary>
    /// 超限时保留的头部字符数。
    /// </summary>
    public const int HeadChars = 4000;

    /// <summary>
    /// 超限时保留的尾部字符数。
    /// </summary>
    public const int TailChars = 8000;

    /// <summary>need22-P0 S5 新增错误码（只增不改前端协议）。</summary>
    public const string InvalidCallIdCode = "INVALID_CALL_ID";

    private static readonly Regex CallIdAllowlist = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 对工具输出执行守卫：未超限原样返回；超限则落盘并返回首尾片段 + 省略提示。
    /// 落盘路径：&lt;DataRoot&gt;/sessions/&lt;workspaceId&gt;/&lt;sessionId&gt;/tool-outputs/call_&lt;callId&gt;.txt
    /// need22-P0 S5：callId 非法直接拒绝落盘（纯截断 + INVALID_CALL_ID 提示）；落盘前脱敏。
    /// </summary>
    public static (string DisplayText, string? SpilledFile) ApplyGuard(
        string? output,
        string dataRoot,
        string workspaceId,
        string sessionId,
        string callId)
    {
        string text = output ?? string.Empty;
        if (text.Length <= MaxInlineChars)
        {
            return (text, null);
        }

        if (!IsValidCallId(callId))
        {
            // 非法 callId：拒绝落盘，回退纯截断并明示错误码
            string truncated = BuildTruncatedText(text, fullPath: null);
            return ($"{InvalidCallIdCode}：callId 非法已拒绝落盘（仅保留首尾片段）。\n{truncated}", null);
        }

        string safeCallId = callId.Trim();

        string spillDir;
        try
        {
            string root = string.IsNullOrWhiteSpace(dataRoot)
                ? Path.GetTempPath()
                : dataRoot;
            spillDir = Path.Combine(root, "sessions", workspaceId, sessionId, "tool-outputs");
            Directory.CreateDirectory(spillDir);
        }
        catch (Exception)
        {
            // 目录创建失败时回退为纯截断（不落盘），保证调用链不断
            return (BuildTruncatedText(text, fullPath: null), null);
        }

        string spillPath = Path.Combine(spillDir, $"call_{safeCallId}.txt");
        // need22-P0 S5：落盘前脱敏（sk-/Bearer 等命中即替换，原文不落地；展示文本同步脱敏）
        var (scrubbed, hits) = SecretScrubber.Scrub(text);
        string shownText = hits > 0 ? scrubbed : text;
        try
        {
            string payload = hits > 0
                ? scrubbed + $"\n\n[注：原文 {hits} 处疑似密钥已脱敏为 [REDACTED_SECRET]，未明文落盘。]"
                : scrubbed;
            File.WriteAllText(spillPath, payload, Utf8NoBom);
        }
        catch (Exception)
        {
            return (BuildTruncatedText(shownText, fullPath: null), null);
        }

        return (BuildTruncatedText(shownText, spillPath), spillPath);
    }

    /// <summary>need22-P0 S5：callId 白名单校验。</summary>
    public static bool IsValidCallId(string? callId) =>
        !string.IsNullOrWhiteSpace(callId) && CallIdAllowlist.IsMatch(callId.Trim());

    private static string BuildTruncatedText(string text, string? fullPath)
    {
        string head = text.Substring(0, Math.Min(HeadChars, text.Length));
        string tail = text.Substring(Math.Max(0, text.Length - TailChars));
        int omitted = Math.Max(0, text.Length - head.Length - tail.Length);

        var sb = new StringBuilder(head.Length + tail.Length + 512);
        sb.Append(head);
        sb.Append("\n\n... [中间省略 ");
        sb.Append(omitted);
        sb.Append(" 字符");
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            sb.Append("，完整输出已保存至日志文件: ");
            sb.Append(fullPath);
            sb.Append("，你可使用 read_file(file_path, offset, limit) 或 grep_search 按需分段查看");
        }
        else
        {
            sb.Append("（完整输出落盘失败，仅保留首尾片段）");
        }

        sb.Append("] ...\n\n");
        sb.Append(tail);
        return sb.ToString();
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // need22-P0 S5：白名单优先；非法直接返回空（调用方拒绝落盘），不再做字符替换放行
        string trimmed = name.Trim();
        if (IsValidCallId(trimmed))
        {
            return trimmed;
        }

        return string.Empty;
    }
}
