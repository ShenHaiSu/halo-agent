using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// edit_file：need14 宽容匹配替换。
/// R1 默认 normalized（换行 / Tab / 行尾空格容错，落盘字节风格不变），exact 与历史行为等价，regex 支持正则锚点；
/// R3 未命中返回结构化诊断（Top-3 相似行 + 空白符可视化 + 可执行建议），一次返回即可定位；
/// R4 成功回吐硬性 ≤ 2000 字符；R5 短锚点 + replace_all 直接拒绝。
/// </summary>
public sealed class EditFileTool : IToolHandler
{
    /// <summary>R4：成功回吐上限（含截断标记）。</summary>
    internal const int MaxSuccessOutputChars = 2000;

    /// <summary>R5：replace_all 时锚点去空白后的最小长度。</summary>
    internal const int MinAnchorCharsForReplaceAll = 8;

    /// <summary>Tab 展开倍数上限（防呆）。</summary>
    internal const int MaxExpandTabs = 16;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public string Name => "edit_file";

    public string Description =>
        "Replace a text block in an existing file. Default match_mode=normalized tolerates CRLF/LF, Tab/space " +
        "and trailing-space differences while writing back in the file's own byte style. " +
        "A miss returns a structured diagnostic (nearest lines + whitespace visualization + next step). " +
        "For very large files prefer edit_lines.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Target file path.\"},"
        + "\"old_string\":{\"type\":\"string\",\"description\":\"Existing text block to replace (include 2-5 lines of surrounding context to ensure uniqueness). Matching is tolerant by default.\"},"
        + "\"new_string\":{\"type\":\"string\",\"description\":\"Replacement text block.\"},"
        + "\"replace_all\":{\"type\":\"boolean\",\"description\":\"If true, replace all occurrences. If false (default), old_string must match exactly once.\"},"
        + "\"match_mode\":{\"type\":\"string\",\"enum\":[\"exact\",\"normalized\",\"regex\"],\"description\":\"exact=strict (legacy behaviour); normalized (default)=tolerate EOL/Tab/trailing-space; regex=treat old_string as a .NET regex over the normalized text.\"},"
        + "\"normalize_eol\":{\"type\":\"boolean\",\"description\":\"Normalize CRLF/CR to LF before matching. Default true.\"},"
        + "\"expand_tabs\":{\"type\":\"integer\",\"description\":\"Expand Tab to N spaces before matching (0=off). Default 4; matching only, file bytes untouched.\"},"
        + "\"trim_trailing_space\":{\"type\":\"boolean\",\"description\":\"Ignore per-line trailing spaces/tabs before matching. Default true.\"},"
        + "\"ignore_leading_whitespace\":{\"type\":\"boolean\",\"description\":\"Ignore per-line leading indentation before matching. Default false.\"}"
        + "},\"required\":[\"file_path\",\"old_string\",\"new_string\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ---------- 参数解析（新参数全部可选，老调用零改动） ----------
            string? filePathArg = null;
            string? oldString = null;
            string? newString = null;
            bool replaceAll = false;
            string matchMode = "normalized";
            bool normalizeEol = true;
            int expandTabs = 4;
            bool trimTrailingSpace = true;
            bool ignoreLeadingWhitespace = false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("file_path", out JsonElement fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    filePathArg = fpEl.GetString();
                }

                if (root.TryGetProperty("old_string", out JsonElement oldEl) && oldEl.ValueKind == JsonValueKind.String)
                {
                    oldString = oldEl.GetString();
                }

                if (root.TryGetProperty("new_string", out JsonElement newEl) && newEl.ValueKind == JsonValueKind.String)
                {
                    newString = newEl.GetString();
                }

                if (root.TryGetProperty("replace_all", out JsonElement allEl) && IsBool(allEl))
                {
                    replaceAll = allEl.GetBoolean();
                }

                if (root.TryGetProperty("match_mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String)
                {
                    matchMode = (modeEl.GetString() ?? "normalized").Trim().ToLowerInvariant();
                }

                if (root.TryGetProperty("normalize_eol", out JsonElement eolEl) && IsBool(eolEl))
                {
                    normalizeEol = eolEl.GetBoolean();
                }

                if (root.TryGetProperty("expand_tabs", out JsonElement tabEl) && tabEl.ValueKind == JsonValueKind.Number
                    && tabEl.TryGetInt32(out int parsedTabs))
                {
                    expandTabs = Math.Clamp(parsedTabs, 0, MaxExpandTabs);
                }

                if (root.TryGetProperty("trim_trailing_space", out JsonElement trimEl) && IsBool(trimEl))
                {
                    trimTrailingSpace = trimEl.GetBoolean();
                }

                if (root.TryGetProperty("ignore_leading_whitespace", out JsonElement leadEl) && IsBool(leadEl))
                {
                    ignoreLeadingWhitespace = leadEl.GetBoolean();
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/old_string/new_string 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(ToolResult.Fail("缺少必需参数 'file_path'。", durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrEmpty(oldString))
            {
                return Task.FromResult(ToolResult.Fail(
                    "缺少必需参数 'old_string'。请先用 read_file 查看文件精确内容后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (newString == null)
            {
                return Task.FromResult(ToolResult.Fail("缺少必需参数 'new_string'。", durationMs: sw.ElapsedMilliseconds));
            }

            if (matchMode is not ("exact" or "normalized" or "regex"))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"match_mode 取值非法：'{matchMode}'。可选值：exact | normalized | regex（默认 normalized）。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            var (allowed, error, fullPath, _) =
                PathSafetyValidator.ValidateWrite(workspaceRoot, filePathArg);
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(error!, durationMs: sw.ElapsedMilliseconds));
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件不存在：'{fullPath}'。edit_file 仅修改已存在文件，新建请用 write_file。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (FileToolShared.LooksBinary(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝编辑二进制文件：'{fullPath}'。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            FileToolShared.DetectedText detected;
            try
            {
                detected = FileToolShared.ReadAllTextWithDetection(fullPath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件读取失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string raw = detected.Content;
            string relative = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);

            var options = matchMode == "exact"
                ? new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = normalizeEol,
                    ExpandTabs = 0,
                    TrimTrailingSpace = false,
                    IgnoreLeadingWhitespace = false,
                }
                : new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = normalizeEol,
                    ExpandTabs = expandTabs,
                    TrimTrailingSpace = trimTrailingSpace,
                    IgnoreLeadingWhitespace = ignoreLeadingWhitespace,
                };

            // ---------- R5：高危锚点拦截（`},` + replace_all 污染 183 处的复盘点） ----------
            if (replaceAll && matchMode != "regex")
            {
                string compact = WhitespaceRun.Replace(oldString, string.Empty);
                if (compact.Length < MinAnchorCharsForReplaceAll)
                {
                    var probe = EditMatchEngine.Normalize(raw, options);
                    int occurrences = EditMatchEngine.FindAll(
                        probe.Text, EditMatchEngine.Normalize(oldString, options).Text).Count;
                    return Task.FromResult(ToolResult.Fail(
                        $"ANCHOR_TOO_SHORT：锚点去空白后仅 {compact.Length} 个字符且 replace_all=true，本次已拒绝"
                        + $"（文件内命中 {occurrences} 处，若放行会批量污染）。"
                        + "请加长锚点（建议 ≥ 8 个非空白字符，含 2-5 行上下文）或改用 edit_lines(offset, limit) 精确按行编辑。",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            // ---------- 匹配 ----------
            var edits = new List<(int Start, int End, string Text)>();
            int matchCount;

            if (matchMode == "regex")
            {
                Regex regex;
                try
                {
                    regex = new Regex(oldString, RegexOptions.Multiline | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"regex 模式下的 old_string 不是合法的 .NET 正则：{ex.Message}。请修正模式或改用 match_mode=normalized。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                var regexNorm = EditMatchEngine.Normalize(raw, new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = normalizeEol,
                    ExpandTabs = 0,
                    TrimTrailingSpace = false,
                    IgnoreLeadingWhitespace = false,
                });

                MatchCollection matches = regex.Matches(regexNorm.Text);
                matchCount = matches.Count;
                if (matchCount > 0 && !replaceAll && matchCount > 1)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"Error: old_string matched {matchCount} times. Please include more surrounding context lines or set replace_all: true.",
                        durationMs: sw.ElapsedMilliseconds));
                }

                foreach (Match m in matches)
                {
                    if (!replaceAll && edits.Count >= 1)
                    {
                        break;
                    }

                    if (m.Length == 0)
                    {
                        return Task.FromResult(ToolResult.Fail(
                            "regex 命中为空长度（模式不含实际文本），edit_file 无法定位替换区间。请在模式中包含待替换的实际内容。",
                            durationMs: sw.ElapsedMilliseconds));
                    }

                    int rawStart = regexNorm.RawStart[m.Index];
                    int rawEnd = regexNorm.RawEnd[m.Index + m.Length - 1];
                    edits.Add((rawStart, rawEnd, m.Result(newString)));
                }
            }
            else
            {
                var haystack = EditMatchEngine.Normalize(raw, options);
                string needle = EditMatchEngine.Normalize(oldString, options).Text;
                if (needle.Length == 0)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "old_string 归一化后为空（仅含空白或换行），无法定位替换区间。请提供包含实际文本的锚点。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                List<EditMatchEngine.MatchSpan> matches = EditMatchEngine.FindAll(haystack.Text, needle);
                matchCount = matches.Count;
                if (matchCount == 0)
                {
                    return Task.FromResult(ToolResult.Fail(
                        BuildNotFoundDiagnostic(relative, raw, oldString, detected, matchMode, options),
                        durationMs: sw.ElapsedMilliseconds));
                }

                if (matchCount > 1 && !replaceAll)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"Error: old_string matched {matchCount} times. Please include more surrounding context lines or set replace_all: true.",
                        durationMs: sw.ElapsedMilliseconds));
                }

                IEnumerable<EditMatchEngine.MatchSpan> chosen = replaceAll ? matches : matches.Take(1);
                foreach (EditMatchEngine.MatchSpan span in chosen)
                {
                    var (start, end) = EditMatchEngine.ToRawSpan(haystack, span);
                    edits.Add((start, end, newString));
                }
            }

            if (edits.Count == 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    BuildNotFoundDiagnostic(relative, raw, oldString, detected, matchMode, options),
                    durationMs: sw.ElapsedMilliseconds));
            }

            // ---------- 只替换目标区：文件其余字节（换行/缩进风格）保持原样 ----------
            var updated = new StringBuilder(raw);
            foreach ((int start, int end, string replacement) in edits.OrderByDescending(e => e.Start))
            {
                string eol = DetectSpanEol(raw, start, end) ?? detected.NewLine;
                string text = FileToolShared.ConvertNewLines(replacement, eol);
                updated.Remove(start, end - start).Insert(start, text);
            }

            string updatedText = updated.ToString();
            try
            {
                FileToolShared.AtomicWriteAllText(fullPath, updatedText, detected.Encoding, detected.Preamble);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件写回失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            // ---------- R4：成功回吐截断 ----------
            var (diff, diffTruncated) = EditDiffBuilder.Build(
                relative,
                FileToolShared.NormalizeToLf(raw),
                FileToolShared.NormalizeToLf(updatedText),
                EditDiffBuilder.DefaultContextLines,
                MaxSuccessOutputChars - 320);

            var sb = new StringBuilder();
            sb.Append("已修改 '").Append(relative).Append("'（替换 ").Append(edits.Count).Append(" 处；Encoding: ")
              .Append(detected.EncodingLabel).Append(", LineEndings: ").Append(detected.NewLineLabel)
              .Append(", match_mode: ").Append(matchMode).AppendLine("）");
            if (diff.Length > 0)
            {
                sb.AppendLine("```diff").Append(diff);
                if (!diff.EndsWith('\n'))
                {
                    sb.AppendLine();
                }

                sb.AppendLine("```");
            }

            if (diffTruncated)
            {
                sb.AppendLine("[truncated: true] diff 已按 2000 字符预算截断，完整区间请用 read_file(offset, limit) 查看。");
            }

            return Task.FromResult(ToolResult.Ok(
                ClampOutput(sb.ToString(), MaxSuccessOutputChars),
                durationMs: sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"edit_file 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }

    private static bool IsBool(JsonElement el) =>
        el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False;

    /// <summary>命中区间内的换行风格（决定 new_string 的落盘换行）；单行命中返回 null 走文件主风格。</summary>
    private static string? DetectSpanEol(string raw, int start, int end)
    {
        if (start < 0 || end > raw.Length || start >= end)
        {
            return null;
        }

        for (int i = start; i < end; i++)
        {
            if (raw[i] == '\n')
            {
                return i > start && raw[i - 1] == '\r' ? "\r\n" : "\n";
            }
        }

        return null;
    }

    /// <summary>R4：硬性把成功输出压到 maxChars 以内，并保留 truncated 标记。</summary>
    internal static string ClampOutput(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        const string notice = "\n... [truncated: true] 输出已截断（上限 2000 字符）；请用 read_file(offset, limit) 查看完整上下文。";
        int keep = maxChars - notice.Length;
        if (keep <= 0)
        {
            return text.Substring(0, maxChars);
        }

        return text.Substring(0, keep) + notice;
    }

    /// <summary>
    /// R3：old_string 未命中时的结构化诊断——Top-3 相似行（行号 + 相似度 + 空白符可视化）+ 一条可执行建议。
    /// </summary>
    private static string BuildNotFoundDiagnostic(
        string relativePath,
        string raw,
        string oldString,
        FileToolShared.DetectedText detected,
        string matchMode,
        EditMatchEngine.MatchOptions options)
    {
        string[] fileLines = EditMatchEngine.SplitLfLines(raw);
        int fileLines2 = fileLines.Length;
        int needleLines = EditMatchEngine.SplitLfLines(FileToolShared.NormalizeToLf(oldString)).Length;
        List<EditMatchEngine.NearestCandidate> nearest = EditMatchEngine.FindNearest(raw, oldString, options, 3);

        var sb = new StringBuilder();
        sb.Append("{\"ok\":false");
        sb.Append(",\"code\":\"OLD_STRING_NOT_FOUND\"");
        sb.Append(",\"file\":").Append(JsonSerializer.Serialize(relativePath));
        sb.Append(",\"file_eol\":").Append(JsonSerializer.Serialize(detected.NewLineLabel));
        sb.Append(",\"file_lines\":").Append(fileLines2);
        sb.Append(",\"old_string_lines\":").Append(needleLines);
        sb.Append(",\"match_mode\":").Append(JsonSerializer.Serialize(matchMode));
        sb.Append(",\"match_flags\":{\"normalize_eol\":").Append(options.NormalizeEol ? "true" : "false")
          .Append(",\"expand_tabs\":").Append(options.ExpandTabs)
          .Append(",\"trim_trailing_space\":").Append(options.TrimTrailingSpace ? "true" : "false")
          .Append(",\"ignore_leading_whitespace\":").Append(options.IgnoreLeadingWhitespace ? "true" : "false")
          .Append('}');

        sb.Append(",\"nearest\":[");
        for (int i = 0; i < nearest.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"line\":").Append(nearest[i].Line)
              .Append(",\"score\":").Append(Math.Round(nearest[i].Score, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"preview\":").Append(JsonSerializer.Serialize(nearest[i].Preview))
              .Append('}');
        }

        sb.Append(']');
        sb.Append(",\"hint\":").Append(JsonSerializer.Serialize(BuildHint(relativePath, nearest, detected, needleLines, matchMode)));
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildHint(
        string relativePath,
        List<EditMatchEngine.NearestCandidate> nearest,
        FileToolShared.DetectedText detected,
        int needleLines,
        string matchMode)
    {
        var parts = new List<string>();
        EditMatchEngine.NearestCandidate? best = nearest.Count > 0 ? nearest[0] : null;

        if (best != null && best.LeadingHasTab)
        {
            parts.Add($"疑似 Tab/空格差异：第 {best.Line} 行行首为 Tab（已按 expand_tabs 归一仍未命中）。");
        }

        if (detected.NewLine == "\r\n")
        {
            parts.Add("文件为 CRLF 换行（已按 normalize_eol 归一）。");
        }

        if (matchMode == "exact")
        {
            parts.Add("当前 match_mode=exact 为字节级严格匹配，可改回默认 normalized 重试。");
        }

        if (best != null)
        {
            int limit = Math.Max(1, needleLines);
            parts.Add(
                $"最相近的是第 {best.Line} 行（相似度 {Math.Round(best.Score, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}），"
                + $"建议改用 edit_lines(file_path=\"{relativePath}\", offset={best.Line}, limit={limit}) 一次成功。");
            if (best.Score >= 0.8 && matchMode != "exact")
            {
                parts.Add("相似度已很高却未命中，通常是行首缩进差异，可加 ignore_leading_whitespace=true 重试。");
            }
        }
        else
        {
            parts.Add("未找到任何相近行，请用 read_file 重新查看目标区域后再改（或直接用 edit_lines 按行号改）。");
        }

        return string.Join(" ", parts);
    }
}
