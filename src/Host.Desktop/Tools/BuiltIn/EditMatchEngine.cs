using System.Text;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need14 R1/R3：edit_file 宽容匹配引擎。
/// 归一化（换行 / Tab / 行尾空格 / 可选行首缩进）后做子串匹配，
/// 并维护“归一化字符索引 → 原始落盘字节区间”的双向映射，
/// 保证命中后只替换目标区，文件其余字节（换行风格、缩进风格）零改动。
/// </summary>
internal static class EditMatchEngine
{
    /// <summary>归一化开关：全部有默认值，exact 模式与历史行为等价。</summary>
    internal sealed class MatchOptions
    {
        /// <summary>比对前把 CRLF / CR 统一为 LF。</summary>
        public bool NormalizeEol { get; init; } = true;

        /// <summary>比对前把 Tab 展开为 N 个空格（0 = 不展开）。</summary>
        public int ExpandTabs { get; init; } = 4;

        /// <summary>比对前裁掉行尾空格 / Tab。</summary>
        public bool TrimTrailingSpace { get; init; } = true;

        /// <summary>比对时忽略行首缩进（只比相对缩进）。</summary>
        public bool IgnoreLeadingWhitespace { get; init; }
    }

    /// <summary>归一化文本 + 原始索引映射（RawStart/RawEnd 与原文本偏移一一对应）。</summary>
    internal sealed class NormalizedText
    {
        public string Text { get; init; } = string.Empty;

        /// <summary>归一化第 i 个字符来源于原文本的起始偏移。</summary>
        public int[] RawStart { get; init; } = Array.Empty<int>();

        /// <summary>归一化第 i 个字符对应的原文本结束偏移（不含）。</summary>
        public int[] RawEnd { get; init; } = Array.Empty<int>();
    }

    /// <summary>归一化文本中的一处命中。</summary>
    internal readonly struct MatchSpan
    {
        public MatchSpan(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }

        public int Length { get; }
    }

    /// <summary>诊断用的相似行候选（行号与 read_file 一致，1-based）。</summary>
    internal sealed class NearestCandidate
    {
        public int Line { get; init; }

        public double Score { get; init; }

        public string Preview { get; init; } = string.Empty;

        public bool LeadingHasTab { get; init; }
    }

    /// <summary>
    /// 归一化文本并生成索引映射。
    /// 规则：按行裁剪行尾空白 → Tab 展开 → 换行归一（CRLF/CR → LF）；
    /// 可选丢弃行首缩进。每个“行内最后可见字符”的结束偏移会向右延伸到行尾，
    /// 确保命中行尾时替换区间覆盖残留的行尾空格。
    /// </summary>
    internal static NormalizedText Normalize(string raw, MatchOptions options)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new NormalizedText();
        }

        var sb = new StringBuilder(raw.Length + (raw.Length / 8) + 16);
        var starts = new List<int>(raw.Length);
        var ends = new List<int>(raw.Length);

        int i = 0;
        int n = raw.Length;
        bool atLineStart = true;
        int lineStart = 0;

        while (i < n)
        {
            char c = raw[i];

            if (c == '\r' || c == '\n')
            {
                int eolStart = i;
                int eolEnd = c == '\r' && i + 1 < n && raw[i + 1] == '\n' ? i + 2 : i + 1;

                if (options.TrimTrailingSpace)
                {
                    TrimTail(sb, starts, ends);
                }

                // 行内最后一个可见字符的终点扩展到行尾（跨过行尾空白）
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                {
                    ends[sb.Length - 1] = eolStart;
                }

                if (options.NormalizeEol)
                {
                    sb.Append('\n');
                    starts.Add(eolStart);
                    ends.Add(eolEnd);
                }
                else
                {
                    for (int k = eolStart; k < eolEnd; k++)
                    {
                        sb.Append(raw[k]);
                        starts.Add(k);
                        ends.Add(k + 1);
                    }
                }

                i = eolEnd;
                atLineStart = true;
                lineStart = i;
                continue;
            }

            if (atLineStart && options.IgnoreLeadingWhitespace && (c == ' ' || c == '\t'))
            {
                // 行首缩进不参与比对：直接丢弃，命中区间仍从行首起算（写回时按 new_string 缩进）
                i++;
                continue;
            }

            bool firstOfLine = atLineStart;
            atLineStart = false;
            // 归一化后该行的第一个字符：起始偏移回退到行首（忽略行首缩进时替换区间覆盖整行，避免缩进残渣）
            int startRef = firstOfLine ? lineStart : i;

            if (c == '\t' && options.ExpandTabs > 0)
            {
                int width = options.ExpandTabs;
                for (int k = 0; k < width; k++)
                {
                    sb.Append(' ');
                    starts.Add(startRef);
                    ends.Add(i + 1);
                }
            }
            else
            {
                sb.Append(c);
                starts.Add(startRef);
                ends.Add(i + 1);
            }

            i++;
        }

        if (options.TrimTrailingSpace)
        {
            TrimTail(sb, starts, ends);
        }

        // 文件末尾最后一行：同样把最后可见字符的终点扩展到 EOF
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
        {
            ends[sb.Length - 1] = n;
        }

        return new NormalizedText
        {
            Text = sb.ToString(),
            RawStart = starts.ToArray(),
            RawEnd = ends.ToArray(),
        };
    }

    /// <summary>统计归一化文本中 needle 的非重叠命中。</summary>
    internal static List<MatchSpan> FindAll(string haystack, string needle)
    {
        var list = new List<MatchSpan>();
        if (string.IsNullOrEmpty(needle) || haystack.Length == 0 || needle.Length > haystack.Length)
        {
            return list;
        }

        int index = 0;
        while (index <= haystack.Length - needle.Length)
        {
            int hit = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            list.Add(new MatchSpan(hit, needle.Length));
            index = hit + needle.Length;
        }

        return list;
    }

    /// <summary>把归一化命中区间映射回原文本字节区间 [start, end)。</summary>
    internal static (int Start, int End) ToRawSpan(NormalizedText normalized, MatchSpan span)
    {
        int last = span.Start + span.Length - 1;
        int start = normalized.RawStart[span.Start];
        int end = normalized.RawEnd[last];
        return end < start ? (start, start) : (start, end);
    }

    /// <summary>
    /// R3：在文件中找与 old_string 最相似的行（Top-N，按行号 1-based）。
    /// 先用旧文本首行做 Bigram 预筛，再对候选块做整体相似度精算，成本 O(行数×行长)。
    /// </summary>
    internal static List<NearestCandidate> FindNearest(
        string rawContent,
        string needle,
        MatchOptions options,
        int topN = 3)
    {
        var results = new List<NearestCandidate>();
        string[] fileLines = SplitLfLines(rawContent);
        if (fileLines.Length == 0)
        {
            return results;
        }

        string[] needleLines = SplitLfLines(FileToolShared.NormalizeToLf(needle));
        if (needleLines.Length == 0)
        {
            return results;
        }

        string anchor = string.Empty;
        foreach (string line in needleLines)
        {
            string key = LineKey(line, options);
            if (key.Length > 0)
            {
                anchor = key;
                break;
            }
        }

        if (anchor.Length == 0)
        {
            return results;
        }

        int blockLen = Math.Clamp(needleLines.Length, 1, 40);
        string needleBlock = string.Join('\n', needleLines.Select(l => LineKey(l, options)));

        var prefilter = new List<(int Index, double Score)>();
        for (int i = 0; i < fileLines.Length; i++)
        {
            double score = Dice(LineKey(fileLines[i], options), anchor);
            if (score >= 0.2)
            {
                prefilter.Add((i, score));
            }
        }

        if (prefilter.Count == 0)
        {
            return results;
        }

        prefilter.Sort((a, b) => b.Score.CompareTo(a.Score));
        int probe = Math.Min(prefilter.Count, 24);
        var refined = new List<(int Index, double Score)>(probe);
        for (int i = 0; i < probe; i++)
        {
            int idx = prefilter[i].Index;
            int take = Math.Min(blockLen, fileLines.Length - idx);
            double score = take <= 1
                ? prefilter[i].Score
                : Dice(needleBlock, string.Join('\n', Enumerable.Range(idx, take).Select(k => LineKey(fileLines[k], options))));
            refined.Add((idx, Math.Max(score, prefilter[i].Score * 0.99)));
        }

        refined.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach ((int index, double score) in refined.Take(Math.Max(1, topN)))
        {
            string line = fileLines[index];
            int lead = 0;
            while (lead < line.Length && (line[lead] == ' ' || line[lead] == '\t'))
            {
                lead++;
            }

            results.Add(new NearestCandidate
            {
                Line = index + 1,
                Score = score,
                Preview = VisualizeWhitespace(line),
                LeadingHasTab = line.AsSpan(0, lead).IndexOf('\t') >= 0,
            });
        }

        return results;
    }

    /// <summary>空白符可视化：行首/行尾空格 → ·，Tab → →，行尾补 ↵（CRLF/LF 都画 ↵）。</summary>
    internal static string VisualizeWhitespace(string line, int maxChars = 160)
    {
        if (line == null)
        {
            return string.Empty;
        }

        if (line.EndsWith('\r'))
        {
            line = line.Substring(0, line.Length - 1);
        }

        int lead = 0;
        while (lead < line.Length && (line[lead] == ' ' || line[lead] == '\t'))
        {
            lead++;
        }

        int trailStart = line.Length;
        while (trailStart > lead && (line[trailStart - 1] == ' ' || line[trailStart - 1] == '\t'))
        {
            trailStart--;
        }

        var sb = new StringBuilder(line.Length + 8);
        for (int i = 0; i < lead; i++)
        {
            sb.Append(line[i] == '\t' ? '→' : '·');
        }

        sb.Append(line, lead, trailStart - lead);

        for (int i = trailStart; i < line.Length; i++)
        {
            sb.Append(line[i] == '\t' ? '→' : '·');
        }

        sb.Append('↵');

        if (sb.Length > maxChars)
        {
            int head = Math.Max(0, maxChars - 44);
            return sb.ToString(0, head) + "…" + sb.ToString(sb.Length - 40, 40);
        }

        return sb.ToString();
    }

    /// <summary>行相似度用的“形状键”：Tab 展开 + 全部空白折叠为单空格 + Trim。</summary>
    internal static string LineKey(string line, MatchOptions options)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        int width = options.ExpandTabs > 0 ? options.ExpandTabs : 4;
        var sb = new StringBuilder(line.Length + 8);
        bool pendingSpace = false;
        foreach (char c in line)
        {
            if (c == '\r' || c == '\n')
            {
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                if (c == '\t')
                {
                    for (int k = 0; k < width; k++)
                    {
                        sb.Append(' ');
                    }
                }

                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Bigram Dice 相似度（0–1），短串退化为相等判定。</summary>
    internal static double Dice(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1d;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0d;
        }

        if (a == b)
        {
            return 1d;
        }

        if (a.Length < 2 || b.Length < 2)
        {
            return 0d;
        }

        var counts = new Dictionary<int, int>(a.Length);
        for (int i = 0; i < a.Length - 1; i++)
        {
            int key = (a[i] << 16) | a[i + 1];
            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
        }

        int hits = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            int key = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(key, out int c) && c > 0)
            {
                counts[key] = c - 1;
                hits++;
            }
        }

        return 2d * hits / (a.Length - 1 + b.Length - 1);
    }

    /// <summary>按 LF 切行并去掉行尾 \r（行号口径与 read_file 完全一致）。</summary>
    internal static string[] SplitLfLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new[] { string.Empty };
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i].Substring(0, lines[i].Length - 1);
            }
        }

        return lines;
    }

    private static void TrimTail(StringBuilder sb, List<int> starts, List<int> ends)
    {
        while (sb.Length > 0 && (sb[sb.Length - 1] == ' ' || sb[sb.Length - 1] == '\t'))
        {
            sb.Length--;
            starts.RemoveAt(starts.Count - 1);
            ends.RemoveAt(ends.Count - 1);
        }
    }
}
