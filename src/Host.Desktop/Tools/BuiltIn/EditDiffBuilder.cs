using System.Text;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// edit_file / edit_lines 共用的 Unified Diff 生成器。
/// 只输出变更区前后 <c>contextLines</c> 行，且硬性限制字符预算，
/// 超限即停止并置 <c>truncated=true</c>（杜绝动辄几万字符的全量回吐）。
/// </summary>
internal static class EditDiffBuilder
{
    internal const int DefaultContextLines = 5;
    internal const int DefaultMaxChars = 1600;

    /// <summary>把 LF 文本切成逻辑行；末尾换行不计为一个空行。</summary>
    private static string[] SplitLogicalLines(string contentLf)
    {
        if (contentLf.Length == 0)
        {
            return Array.Empty<string>();
        }

        string[] parts = contentLf.Split('\n');
        if (parts.Length > 0 && parts[parts.Length - 1].Length == 0)
        {
            Array.Resize(ref parts, parts.Length - 1);
        }

        return parts;
    }

    /// <summary>生成单 hunk Unified Diff（内容 LF 口径，输出行以 \n 结束）。</summary>
    internal static (string Diff, bool Truncated) Build(
        string relativePath,
        string oldContentLf,
        string newContentLf,
        int contextLines = DefaultContextLines,
        int maxChars = DefaultMaxChars
    )
    {
        string[] oldLines = SplitLogicalLines(oldContentLf);
        string[] newLines = SplitLogicalLines(newContentLf);

        int prefix = 0;
        while (
            prefix < oldLines.Length
            && prefix < newLines.Length
            && oldLines[prefix] == newLines[prefix]
        )
        {
            prefix++;
        }

        int suffix = 0;
        while (
            suffix < oldLines.Length - prefix
            && suffix < newLines.Length - prefix
            && oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix]
        )
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
        int oldCtxEnd = Math.Min(oldLines.Length, oldChangeStart + oldChangeCount - 1 + ctx);
        int newCtxStart = Math.Max(1, newChangeStart - ctx);
        int newCtxEnd = Math.Min(newLines.Length, newChangeStart + newChangeCount - 1 + ctx);

        int budget = Math.Max(200, maxChars);
        var sb = new StringBuilder(budget + 256);
        bool truncated = false;

        sb.Append("--- a/").Append(relativePath).Append('\n');
        sb.Append("+++ b/").Append(relativePath).Append('\n');
        sb.Append("@@ -")
            .Append(oldCtxStart)
            .Append(',')
            .Append(Math.Max(0, oldCtxEnd - oldCtxStart + 1));
        sb.Append(" +")
            .Append(newCtxStart)
            .Append(',')
            .Append(Math.Max(0, newCtxEnd - newCtxStart + 1));
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

        // 变更区为空（纯插入）时偏移为零，尾部上下文从插入点后首行开始，不得跳行。
        for (int i = oldChangeStart + oldChangeCount; i <= oldCtxEnd; i++)
        {
            if (!TryAppend(' ', oldLines[i - 1]))
            {
                return (sb.ToString(), true);
            }
        }

        return (sb.ToString(), truncated);
    }

    public sealed record BatchSpan(int StartLine, int OldCount, int NewCount);

    public static (string Diff, bool Truncated) BuildMulti(
        string relative,
        string oldContentLf,
        string newContentLf,
        IReadOnlyList<BatchSpan> spans,
        int budget = DefaultMaxChars
    )
    {
        if (spans == null || spans.Count == 0)
        {
            return Build(relative, oldContentLf, newContentLf, DefaultContextLines, budget);
        }

        string[] oldLines = SplitLogicalLines(oldContentLf);
        string[] newLines = SplitLogicalLines(newContentLf);
        int ctx = DefaultContextLines;

        List<BatchSpan> ordered = spans.OrderBy(s => s.StartLine).ToList();

        // 按行号排序后，把上下文区间相交的编辑合并到同一片段再渲染。
        var hunkStarts = new List<int>();
        var hunkEnds = new List<int>();
        var hunkMembers = new List<List<BatchSpan>>();
        foreach (var s in ordered)
        {
            int start = Math.Max(1, s.StartLine - ctx);
            int changeEnd = s.OldCount > 0 ? s.StartLine + s.OldCount - 1 : s.StartLine - 1;
            int end = Math.Min(oldLines.Length, changeEnd + ctx);
            if (oldLines.Length == 0)
            {
                start = 1;
                end = 0;
            }

            if (hunkStarts.Count > 0 && start <= hunkEnds[hunkEnds.Count - 1] + 1)
            {
                if (end > hunkEnds[hunkEnds.Count - 1])
                {
                    hunkEnds[hunkEnds.Count - 1] = end;
                }

                hunkMembers[hunkMembers.Count - 1].Add(s);
            }
            else
            {
                hunkStarts.Add(start);
                hunkEnds.Add(end);
                hunkMembers.Add(new List<BatchSpan> { s });
            }
        }

        int limit = Math.Max(200, budget);
        var sb = new StringBuilder(limit + 256);
        sb.Append("--- a/").Append(relative).Append('\n');
        sb.Append("+++ b/").Append(relative).Append('\n');

        bool truncated = false;
        int delta = 0;
        int hunkIndex = 0;

        bool TryAppend(char kind, string line)
        {
            if (sb.Length + line.Length + 2 > limit)
            {
                truncated = true;
                return false;
            }

            sb.Append(kind).Append(line).Append('\n');
            return true;
        }

        string LineOf(string[] lines, int oneBased) =>
            oneBased >= 1 && oneBased <= lines.Length ? lines[oneBased - 1] : string.Empty;

        for (; hunkIndex < hunkStarts.Count; hunkIndex++)
        {
            int oldStart = hunkStarts[hunkIndex];
            int oldEnd = hunkEnds[hunkIndex];
            List<BatchSpan> members = hunkMembers[hunkIndex];

            int hunkDelta = 0;
            foreach (var m in members)
            {
                hunkDelta += m.NewCount - m.OldCount;
            }

            int oldCount = Math.Max(0, oldEnd - oldStart + 1);
            // 新文件侧起点按此前全部编辑的行数差折算，保证头部与正文自洽。
            int newStart = oldStart + delta;
            int newCount = oldCount + hunkDelta;

            sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount);
            sb.Append(" +").Append(newStart).Append(',').Append(newCount);
            sb.Append(" @@\n");

            int oldPtr = oldStart;
            int newPtr = newStart;
            bool broken = false;
            foreach (var m in members)
            {
                for (int i = oldPtr; i < m.StartLine; i++)
                {
                    if (!TryAppend(' ', LineOf(oldLines, i)))
                    {
                        broken = true;
                        break;
                    }

                    oldPtr++;
                    newPtr++;
                }

                if (broken)
                {
                    break;
                }

                for (int j = 0; j < m.OldCount; j++)
                {
                    if (!TryAppend('-', LineOf(oldLines, m.StartLine + j)))
                    {
                        broken = true;
                        break;
                    }
                }

                if (broken)
                {
                    break;
                }

                oldPtr = Math.Max(oldPtr, m.StartLine + m.OldCount);
                for (int j = 0; j < m.NewCount; j++)
                {
                    if (!TryAppend('+', LineOf(newLines, newPtr + j)))
                    {
                        broken = true;
                        break;
                    }
                }

                if (broken)
                {
                    break;
                }

                newPtr += m.NewCount;
            }

            if (!broken)
            {
                for (int i = oldPtr; i <= oldEnd; i++)
                {
                    if (!TryAppend(' ', LineOf(oldLines, i)))
                    {
                        broken = true;
                        break;
                    }

                    newPtr++;
                }
            }

            delta += hunkDelta;
            if (broken)
            {
                break;
            }
        }

        // 截断不断尾，追加一行提示，注明还有几个片段未预览。
        if (truncated)
        {
            int rest = hunkStarts.Count - hunkIndex;
            if (rest < 1)
            {
                rest = 1;
            }

            sb.Append("…（已截断，另有 ").Append(rest).Append(" 段未预览）\n");
        }

        return (sb.ToString(), truncated);
    }
}
