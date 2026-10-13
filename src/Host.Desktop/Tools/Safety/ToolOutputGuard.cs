using System.Text;

namespace Host.Desktop.Tools.Safety;

/// <summary>
/// 超长工具输出自动截断与落盘转存守卫（Output Spillover Protection）。
/// 单次内联返回上限 16,000 字符（约 4,000 Tokens）；超限则完整输出落盘至会话 tool-outputs 目录，
/// 上下文仅保留头部 4,000 + 省略提示 + 尾部 8,000 字符（编译错误与退出状态多在尾部，尾部权重更大）。
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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 对工具输出执行守卫：未超限原样返回；超限则落盘并返回首尾片段 + 省略提示。
    /// 落盘路径：&lt;DataRoot&gt;/sessions/&lt;workspaceId&gt;/&lt;sessionId&gt;/tool-outputs/call_&lt;callId&gt;.txt
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

        string safeCallId = SanitizeFileName(callId);
        if (string.IsNullOrWhiteSpace(safeCallId))
        {
            safeCallId = Guid.NewGuid().ToString("N");
        }

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
        try
        {
            File.WriteAllText(spillPath, text, Utf8NoBom);
        }
        catch (Exception)
        {
            return (BuildTruncatedText(text, fullPath: null), null);
        }

        return (BuildTruncatedText(text, spillPath), spillPath);
    }

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

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = sb.ToString();
        return result.Length > 64 ? result.Substring(0, 64) : result;
    }
}
