using System.Text;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need14 R4：edit_file / edit_lines 共用的 Unified Diff 生成器。
/// 只输出变更区前后 <c>contextLines</c> 行，且硬性限制字符预算，
/// 超限即停止并置 <c>truncated=true</c>（杜绝动辄几万字符的全量回吐）。
/// </summary>
internal static class EditDiffBuilder
{
    internal const int DefaultContextLines = 5;
    internal const int DefaultMaxChars = 1600;

    /// <summary>生成单 hunk Unified Diff（内容 LF 口径，输出行以 \n 结束）。</summary>
    internal static (string Diff, bool Truncated) Build(
        string relativePath,
        string oldContentLf,
        string newContentLf,
        int contextLines = DefaultContextLines,
        int maxChars = DefaultMaxChars)
    {
        string[] oldLines = oldContentLf.Split('\n');
        string[] newLines = newContentLf.Split('\n');

        int prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
        {
            suffix++;
        }

        int oldChangeStart = prefix + 1; // 1-based
        int oldChangeCount = oldLines.Length - prefix - suffix;
        int newChangeStart = prefix + 1; // 1-based
        int newChangeCount = newLines.Length - prefix - suffix;

        if (oldChangeCount <= 0 && newChangeCount <= 0)
        {
            return (string.Empty, false);
        }

        int ctx = Math.Max(0, contextLines);
        int oldCtxStart = Math.Max(1, oldChangeStart - ctx);
        int oldCtxEnd = Math.Min(oldLines.Length, oldChangeStart + Math.Max(oldChangeCount, 1) - 1 + ctx);
        int newCtxStart = Math.Max(1, newChangeStart - ctx);
        int newCtxEnd = Math.Min(newLines.Length, newChangeStart + Math.Max(newChangeCount, 1) - 1 + ctx);

        int budget = Math.Max(200, maxChars);
        var sb = new StringBuilder(budget + 256);
        bool truncated = false;

        sb.Append("--- a/").Append(relativePath).Append('\n');
        sb.Append("+++ b/").Append(relativePath).Append('\n');
        sb.Append("@@ -").Append(oldCtxStart).Append(',').Append(Math.Max(0, oldCtxEnd - oldCtxStart + 1));
        sb.Append(" +").Append(newCtxStart).Append(',').Append(Math.Max(0, newCtxEnd - newCtxStart + 1));
        sb.Append(" @@\n");

        bool TryAppend(char kind, string line)
        {
            if (sb.Length + line.Length + 2 > budget)
            {
                truncated = true;
                return false;
            }

            sb.Append(kind).Append(line).Append('\n');
            return true;
        }

        for (int i = oldCtxStart; i < oldChangeStart; i++)
        {
            if (!TryAppend(' ', oldLines[i - 1]))
            {
                return (sb.ToString(), true);
            }
        }

        for (int i = 0; i < oldChangeCount; i++)
        {
            if (!TryAppend('-', oldLines[oldChangeStart - 1 + i]))
            {
                return (sb.ToString(), true);
            }
        }

        for (int i = 0; i < newChangeCount; i++)
        {
            if (!TryAppend('+', newLines[newChangeStart - 1 + i]))
            {
                return (sb.ToString(), true);
            }
        }

        for (int i = oldChangeStart + Math.Max(oldChangeCount, 1); i <= oldCtxEnd; i++)
        {
            if (!TryAppend(' ', oldLines[i - 1]))
            {
                return (sb.ToString(), true);
            }
        }

        return (sb.ToString(), truncated);
    }
}
