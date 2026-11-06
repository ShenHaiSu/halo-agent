using System.Security.Cryptography;
using System.Text;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// edit / write 面向模型的反馈文案与行号、hash 口径。
/// hash 口径：SHA256(文件 UTF-8 字节，无 BOM 归一)前 8 hex 小写；mtime+size 不用。
/// 行号口径：显示从 1 起，调用 offset 从 0 起。
/// </summary>
public static class EditFeedback
{
    public const int MultiHitMaxChars = 2000;
    public const int SuccessSurroundMaxChars = 1000;
    public const int SuccessSurroundRadius = 5;

    public static string DetectNewlineKind(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : "LF";

    public static bool HasBom(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;

    public static string ComputeShortHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return ComputeShortHashForBytes(bytes);
    }

    public static string ComputeShortHashForBytes(byte[] raw)
    {
        ReadOnlySpan<byte> span = raw;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(span, hash);
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    public static int CountLines(string content)
    {
        if (content.Length == 0) return 0;
        int n = 0;
        foreach (var c in content)
            if (c == '\n') n++;
        return content.EndsWith('\n') ? n : n + 1;
    }

    public static List<int> FindOccurrences(string content, string needle)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(needle)) return list;
        int idx = 0;
        while ((idx = content.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            list.Add(idx);
            idx += needle.Length;
            if (idx >= content.Length) break;
        }
        return list;
    }

    /// <summary>每行起始偏移（含第 1 行 0）。</summary>
    public static int[] GetLineStartOffsets(string content)
    {
        if (content.Length == 0) return Array.Empty<int>();
        var list = new List<int> { 0 };
        for (int i = 0; i < content.Length; i++)
            if (content[i] == '\n' && i + 1 < content.Length)
                list.Add(i + 1);
        return list.ToArray();
    }

    public static int IndexToLine(int[] starts, int index)
    {
        int lo = 0, hi = starts.Length - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (starts[mid] <= index) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans + 1;
    }

    public static string SummarizeLine(string line, int max = 80)
    {
        var s = line.Trim();
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }

    public static string GetLineText(string content, int lineOneBased)
    {
        var lines = content.Split('\n');
        if (lineOneBased < 1 || lineOneBased > lines.Length) return string.Empty;
        return lines[lineOneBased - 1].TrimEnd('\r');
    }

    public static string BuildZeroMatchMessage(
        string path, int totalLines, string newlineKind, string shortHash, bool hasBom,
        int pointerOffset, int pointerLimit)
    {
        var sb = new StringBuilder();
        sb.Append($"未找到 old_string（0 匹配）。文件 {path} 共 {totalLines} 行，换行 {newlineKind}，hash {shortHash}。\n");
        sb.Append($"续读核对：read_file(path=\"{path}\", offset={pointerOffset}, limit={pointerLimit})\n");
        sb.Append("提示：行号前缀不是内容；");
        if (newlineKind == "CRLF")
            sb.Append("CRLF 文件需含 \\r\\n；");
        if (hasBom)
            sb.Append("文件含 BOM 头，old_string 无需含 BOM；");
        sb.Append("若刚读过截断视图，请重读该窗口后原样抄入。");
        return sb.ToString();
    }

    public static string BuildMultiMatchMessage(
        string path, IReadOnlyList<(int StartLine, int EndLine, string Summary)> hits,
        int pointerOffset, int pointerLimit)
    {
        var sb = new StringBuilder();
        sb.Append($"old_string 在文件中出现 {hits.Count} 次，未写入。请加上下文使其唯一。命中行：\n");
        foreach (var h in hits)
        {
            var line = $"- 第 {h.StartLine}-{h.EndLine} 行：{h.Summary}\n";
            if (sb.Length + line.Length > MultiHitMaxChars)
            {
                sb.Append("…(命中过多已截断，请加上下文缩小范围)\n");
                break;
            }
            sb.Append(line);
        }
        sb.Append($"重读其一确认：read_file(path=\"{path}\", offset={pointerOffset}, limit={pointerLimit})");
        return sb.ToString();
    }

    public static string BuildSuccessMessage(
        string path, int startLine, int endLine, string shortHash, string surrounding)
    {
        var shown = surrounding.Length <= SuccessSurroundMaxChars
            ? surrounding
            : surrounding[..SuccessSurroundMaxChars] + "…";
        return $"已替换 {path} 第 {startLine}-{endLine} 行 1 处（新 hash {shortHash}）。改后周边 ≤5 行（总 ≤1000 字符）：{shown}";
    }

    public static string BuildWriteSuccessMessage(
        string path, int bytes, int lines, string shortHash, string createdParentNote) =>
        $"已写入 {path}（{bytes} 字节，{lines} 行，新 hash {shortHash}）{createdParentNote}";

    public static string ExtractSurrounding(string content, int startLine, int endLine, int radius = SuccessSurroundRadius)
    {
        int total = CountLines(content);
        if (total == 0) return string.Empty;
        int from = Math.Max(1, startLine - radius);
        int to = Math.Min(total, endLine + radius);
        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            sb.Append(GetLineText(content, i));
            if (i < to) sb.Append('\n');
            if (sb.Length > SuccessSurroundMaxChars) break;
        }
        var s = sb.ToString();
        return s.Length <= SuccessSurroundMaxChars ? s : s[..SuccessSurroundMaxChars] + "…";
    }
}
