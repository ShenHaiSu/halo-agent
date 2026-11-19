using System.Text;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// 批量编辑回执生成器：纯函数，不触碰文件、不依赖工具上下文。
/// 输入落盘后的最终文本与每个 edit 的写入事实，输出结构化回执与人类可读摘要，
/// 使单测可脱离真实文件系统直接验证计数与文本一致性。
/// </summary>
internal static class EditReceiptBuilder
{
    /// <summary>单个 edit 落盘后的已知事实，由 edit_batch 在各返回路径收集。</summary>
    internal sealed record AppliedEdit(
        int Index,
        int AnchorOccurrences,
        int StartOffset, // 写入后文本中的起始字符偏移
        int WrittenLength, // 写入文本长度（字符，含目标 EOL）
        int ReplacedLines, // 被替换的原文件行数
        string ResultingText // 写入文本（已转目标 EOL）
    );

    /// <summary>摘要中单个 resulting_text 的预览字符预算，超出部分指向结构化字段。</summary>
    private const int PreviewTextChars = 640;

    /// <summary>
    /// 按 SplitLogicalLines 口径统计逻辑行数：先归一化为 LF，末尾换行不额外计一行。
    /// </summary>
    internal static int CountTextLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        string lf = FileToolShared.NormalizeToLf(text);
        int breaks = 0;
        foreach (char c in lf)
        {
            if (c == '\n')
            {
                breaks++;
            }
        }

        return lf.EndsWith('\n') ? breaks : breaks + 1;
    }

    /// <summary>统计原文命中区间的行数（与落盘文本同口径）。</summary>
    internal static int CountSpanLines(string raw, int start, int end) =>
        CountTextLines(raw.Substring(start, end - start));

    /// <summary>
    /// 在写入后的文本 updated 上定位 edit 的 1-based 起止行。
    /// 纯插入（length==0）返回 [n, n-1] 空区间锚点，由 written_lines 表达长度。
    /// </summary>
    internal static int[] LinesOfOffset(string updated, int startOffset, int length)
    {
        int startLine = 1;
        int limit = Math.Min(startOffset, updated.Length);
        for (int i = 0; i < limit; i++)
        {
            if (updated[i] == '\n')
            {
                startLine++;
            }
        }

        if (length == 0)
        {
            return new[] { startLine, startLine - 1 };
        }

        // 以落盘后文本的实际区间为准算结束行，口径与 CountTextLines 一致（末尾换行不计新行），
        // 保证 at_lines 跨度 == written_lines，避免写入文本以换行结尾时多算一行。
        int from = Math.Min(startOffset, updated.Length);
        int to = Math.Min(startOffset + length, updated.Length);
        int writtenLines = CountTextLines(updated.Substring(from, to - from));
        return new[] { startLine, startLine + writtenLines - 1 };
    }

    /// <summary>由落盘文本与每个 edit 的写入事实生成结构化回执。</summary>
    public static EditReceipt Build(
        string relative,
        string mode,
        bool dryRun,
        string updated,
        FileToolShared.DetectedText detected,
        string hashAfter,
        bool expectedHashChecked,
        string? backup,
        IReadOnlyList<AppliedEdit> applied
    )
    {
        var edits = applied
            .Select(a =>
            {
                int[] lines = LinesOfOffset(updated, a.StartOffset, a.WrittenLength);
                return new EditReceiptEdit(
                    a.Index,
                    a.AnchorOccurrences,
                    lines,
                    a.ReplacedLines,
                    CountTextLines(a.ResultingText),
                    a.ResultingText
                );
            })
            .ToList();

        return new EditReceipt(
            true,
            "edit_batch",
            relative,
            mode,
            dryRun,
            edits,
            new EditReceiptVerify(
                expectedHashChecked,
                hashAfter,
                detected.EncodingLabel,
                detected.NewLineLabel,
                CountTextLines(updated),
                applied.Select(e => e.Index).Distinct().Count() == applied.Count
            ),
            backup
        );
    }

    /// <summary>
    /// 按 edit 分块生成摘要文本（不含头部元信息）。超长 resulting_text 截断并指向结构化字段，
    /// 真相数据本身仍完整保留在 SuccessPayloadJson 中。
    /// </summary>
    public static string BuildDigest(EditReceipt r)
    {
        var sb = new StringBuilder();
        foreach (var e in r.Edits)
        {
            int start = e.AtLines.Length > 0 ? e.AtLines[0] : 0;
            int end = e.AtLines.Length > 1 ? e.AtLines[1] : 0;
            sb.Append("edit[")
                .Append(e.Index)
                .Append("] @L")
                .Append(start)
                .Append('-')
                .Append(end)
                .Append("   anchor×")
                .Append(e.AnchorOccurrences)
                .Append("  ")
                .Append(e.ReplacedLines)
                .Append(" 行 → ")
                .Append(e.WrittenLines)
                .Append(" 行\n");
            AppendWrittenPreview(sb, e.ResultingText, e.WrittenLines);
        }

        sb.Append("verify: expected_hash ")
            .Append(r.Verify.ExpectedHashChecked ? "✓" : "—")
            .Append("  disjoint ")
            .Append(r.Verify.AllEditsMutuallyDisjoint ? "✓" : "✗")
            .Append("  total ")
            .Append(r.Verify.TotalLines)
            .Append(" 行")
            .Append("  hash_after=")
            .Append(FileToolShared.TruncateHash(r.Verify.HashAfter))
            .Append('\n');
        return sb.ToString();
    }

    private static void AppendWrittenPreview(
        StringBuilder sb,
        string resultingText,
        int writtenLines
    )
    {
        string lf = FileToolShared.NormalizeToLf(resultingText);
        string[] rows = lf.Length == 0 ? Array.Empty<string>() : lf.Split('\n');
        if (rows.Length > 0 && rows[rows.Length - 1].Length == 0)
        {
            Array.Resize(ref rows, rows.Length - 1);
        }

        sb.Append("  written: ");
        if (rows.Length == 0)
        {
            sb.Append("(空)\n");
            return;
        }

        int used = 0;
        bool truncated = false;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("\n           ");
            }

            string row = rows[i];
            if (used + row.Length > PreviewTextChars)
            {
                int remain = Math.Max(0, PreviewTextChars - used);
                sb.Append(row.Substring(0, Math.Min(remain, row.Length)));
                truncated = true;
                break;
            }

            sb.Append(row);
            used += row.Length;
        }

        sb.Append('\n');
        if (truncated)
        {
            sb.Append("           …(共 ")
                .Append(writtenLines)
                .Append(" 行，完整内容见 SuccessPayloadJson.resulting_text)\n");
        }
    }
}
