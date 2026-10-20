using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// edit_file：need14 宽容匹配替换 + need15 安全加固。
/// R1 默认 normalized，R3 结构化诊断，R4 回吐≤2000，R5 短锚拦截；
/// need15 P0-1 regex $ 默认字面（regex_expand_groups 显式开启展开）；
/// P0-4 模糊多命中 ANCHOR_AMBIGUOUS；P0-5 结构化错误码；P1-2 Schema 收敛 + 短锚拦截；
/// P1-3 成功回吐 hash；P2-1 dry_run；P2-2 每次覆盖备份。
/// </summary>
public sealed class EditFileTool : IToolHandler
{
    /// <summary>R4：成功回吐上限（含截断标记）。</summary>
    internal const int MaxSuccessOutputChars = 2000;

    /// <summary>R5：replace_all 时锚点去空白后的最小长度。</summary>
    internal const int MinAnchorCharsForReplaceAll = 8;

    /// <summary>need15 P1-2：通用短锚拦截阈值（去空白后非空白字符数）。</summary>
    internal const int MinAnchorNonBlankChars = 4;

    /// <summary>need15 P0-4：replace_all 批量警告阈值（可热调）。</summary>
    internal static int MaxBulkWarnThreshold = 20;

    /// <summary>Tab 展开倍数上限（防呆）。</summary>
    internal const int MaxExpandTabs = 16;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public string Name => "edit_file";

    // NEED17 PromptBrief: Anchor-based partial edit
    // NEED17 PromptRules: old_string must be verbatim copy-paste from read_file; multiple anchors need multiple calls; merge adjacent edits.
    public string Description =>
        "Replace a text block in an existing file. Default match_mode=normalized tolerates CRLF/LF, Tab/space " +
        "and trailing-space differences while writing back in the file's own byte style. " +
        "A miss returns a structured diagnostic (nearest lines + whitespace visualization + next step). " +
        "For very large files prefer edit_lines. For regex mode, $ is literal by default. " +
        "For single-file small edits (multi-anchor in one call is not supported; use multiple calls or edit_lines). Large refactors use write_file. " +
        "Example: {\"file_path\":\"src/a.ts\",\"old_string\":\"  if (x) {\\n    foo();\\n  }\",\"new_string\":\"  if (x) {\\n    bar();\\n  }\"}";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString("file_path", "Target file path.", required: true)
            .AddString("old_string", "Verbatim copy-paste from read_file (2-5 context lines for uniqueness). Min 16 chars.", required: true, minLength: 16)
            .AddString("new_string", "Replacement text. $ is literal unless regex_expand_groups:true.", required: true)
            .AddBoolean("replace_all", "If true, replace all occurrences. If false (default), old_string must match exactly once.", defaultValue: false)
            .AddString("match_mode", "exact=strict (legacy behaviour); normalized (default)=tolerate EOL/Tab/trailing-space; regex=treat old_string as a .NET regex over the normalized text.", enumValues: new string[] { "normalized", "exact", "regex" }, defaultValue: "normalized")
            .AddBoolean("regex_expand_groups", "Only for match_mode=regex. If true, expand $1/${name} in new_string. Default false (literal write, safe for $ in code).")
            .AddString("expected_hash", "Optional sha256 of the file bytes; mismatch rejects the edit to avoid concurrent overwrite.")
            .AddBoolean("dry_run", "If true, only compute and preview the diff without writing.")
            .Build();
        return new(Name, Description, schema);
    }

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ---------- 参数解析（新参数全部可选，老调用零改动；隐藏高级旋钮仍解析） ----------
            string? filePathArg = null;
            string? oldString = null;
            string? newString = null;
            bool replaceAll = false;
            string matchMode = "normalized";
            bool normalizeEol = true;
            int expandTabs = 4;
            bool trimTrailingSpace = true;
            bool ignoreLeadingWhitespace = false;
            bool regexExpandGroups = false;
            string? expectedHash = null;
            bool dryRun = false;

            try
            {
                // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口）
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("参数不是合法 JSON 对象。");
                }
                filePathArg = args.GetString("file_path");
                oldString = args.GetString("old_string");
                newString = args.GetString("new_string");
                if (args.TryGetBool("replace_all", out bool all))
                {
                    replaceAll = all;
                }
                string? mode = args.GetString("match_mode");
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    matchMode = mode.Trim().ToLowerInvariant();
                }
                if (args.TryGetBool("normalize_eol", out bool eol))
                {
                    normalizeEol = eol;
                }
                if (args.TryGetInt32("expand_tabs", out int parsedTabs))
                {
                    expandTabs = Math.Clamp(parsedTabs, 0, MaxExpandTabs);
                }
                if (args.TryGetBool("trim_trailing_space", out bool trim))
                {
                    trimTrailingSpace = trim;
                }
                if (args.TryGetBool("ignore_leading_whitespace", out bool lead))
                {
                    ignoreLeadingWhitespace = lead;
                }
                if (args.TryGetBool("regex_expand_groups", out bool exp))
                {
                    regexExpandGroups = exp;
                }
                expectedHash = args.GetString("expected_hash");
                if (args.TryGetBool("dry_run", out bool dry))
                {
                    dryRun = dry;
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
                    $"文件不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。edit_file 仅修改已存在文件，新建请用 write_file。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (FileToolShared.LooksBinary(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝编辑二进制文件：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。",
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

            // ---------- need15 P1-2：通用短锚拦截（非 regex） ----------
            if (matchMode != "regex")
            {
                string needleProbe = EditMatchEngine.Normalize(oldString, options).Text;
                string compactProbe = WhitespaceRun.Replace(needleProbe, string.Empty);
                if (needleProbe.Length == 0 || compactProbe.Length < MinAnchorNonBlankChars)
                {
                    return Task.FromResult(ToolResult.FailCode("ANCHOR_TOO_SHORT",
                        $"ANCHOR_TOO_SHORT：锚点过短（去空白后仅 {compactProbe.Length} 个字符，需 ≥ {MinAnchorNonBlankChars}）。请加长锚点（含 2-5 行上下文）或改用 edit_lines(offset, limit) 精确按行编辑。",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            // ---------- R5：高危锚点拦截（`},` + replace_all 污染 183 处的复盘点） ----------
            if (replaceAll && matchMode != "regex")
            {
                string compact = WhitespaceRun.Replace(oldString, string.Empty);
                if (compact.Length < MinAnchorCharsForReplaceAll)
                {
                    var probe = EditMatchEngine.Normalize(raw, options);
                    int occurrences = EditMatchEngine.FindAll(
                        probe.Text, EditMatchEngine.Normalize(oldString, options).Text).Count;
                    return Task.FromResult(ToolResult.FailCode("ANCHOR_TOO_SHORT",
                        $"ANCHOR_TOO_SHORT：锚点去空白后仅 {compact.Length} 个字符且 replace_all=true，本次已拒绝"
                        + $"（文件内命中 {occurrences} 处，若放行会批量污染）。"
                        + "请加长锚点（建议 ≥ 8 个非空白字符，含 2-5 行上下文）或改用 edit_lines(offset, limit) 精确按行编辑。",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            // ---------- need15：expected_hash 防并发覆盖 ----------
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                string currentHash;
                try
                {
                    currentHash = FileToolShared.ComputeHash(File.ReadAllBytes(fullPath));
                }
                catch
                {
                    currentHash = FileToolShared.ComputeTextHash(detected.Content, detected);
                }

                string want = NormalizeHash(expectedHash);
                if (!string.Equals(want, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ToolResult.FailCode("EXPECTED_HASH_MISMATCH",
                        $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前文件 hash: {FileToolShared.TruncateHash(currentHash)}，完整 hash 请用 read_file 查看；期望: {want}）。"
                        + "请用 read_file 重新确认内容后重试，或不传 expected_hash。",
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
                if (matchCount == 0)
                {
                    string diag = BuildNotFoundDiagnostic(relative, raw, oldString, detected, matchMode, options);
                    return Task.FromResult(ToolResult.FailCode("OLD_STRING_NOT_FOUND",
                        "OLD_STRING_NOT_FOUND：" + diag, diag,
                        durationMs: sw.ElapsedMilliseconds));
                }

                if (!replaceAll && matchCount > 1)
                {
                    return Task.FromResult(BuildAmbiguousResult(
                        relative, raw, oldString, detected, matchMode, options, matchCount, sw.ElapsedMilliseconds));
                }

                foreach (Match m in matches)
                {
                    if (!replaceAll && edits.Count >= 1)
                    {
                        break;
                    }

                    if (m.Length == 0)
                    {
                        return Task.FromResult(ToolResult.FailCode("REGEX_ZERO_LENGTH",
                            "REGEX_ZERO_LENGTH：regex 命中为空长度（模式不含实际文本），edit_file 无法定位替换区间。请在模式中包含待替换的实际内容。",
                            durationMs: sw.ElapsedMilliseconds));
                    }

                    int rawStart = regexNorm.RawStart[m.Index];
                    int rawEnd = regexNorm.RawEnd[m.Index + m.Length - 1];
                    string replacement = regexExpandGroups ? m.Result(newString) : newString;
                    edits.Add((rawStart, rawEnd, replacement));
                }
            }
            else
            {
                var haystack = EditMatchEngine.Normalize(raw, options);
                string needle = EditMatchEngine.Normalize(oldString, options).Text;
                if (needle.Length == 0)
                {
                    return Task.FromResult(ToolResult.FailCode("ANCHOR_TOO_SHORT",
                        "ANCHOR_TOO_SHORT：old_string 归一化后为空（仅含空白或换行），无法定位替换区间。请提供包含实际文本的锚点。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                List<EditMatchEngine.MatchSpan> matches = EditMatchEngine.FindAll(haystack.Text, needle);
                matchCount = matches.Count;
                if (matchCount == 0)
                {
                    string diag = BuildNotFoundDiagnostic(relative, raw, oldString, detected, matchMode, options);
                    return Task.FromResult(ToolResult.FailCode("OLD_STRING_NOT_FOUND",
                        "OLD_STRING_NOT_FOUND：" + diag, diag,
                        durationMs: sw.ElapsedMilliseconds));
                }

                if (matchCount > 1 && !replaceAll)
                {
                    return Task.FromResult(BuildAmbiguousResult(
                        relative, raw, oldString, detected, matchMode, options, matchCount, sw.ElapsedMilliseconds));
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
                string diag = BuildNotFoundDiagnostic(relative, raw, oldString, detected, matchMode, options);
                return Task.FromResult(ToolResult.FailCode("OLD_STRING_NOT_FOUND",
                    "OLD_STRING_NOT_FOUND：" + diag, diag,
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
            string newHashPreview = FileToolShared.ComputeTextHash(updatedText, detected);

            var (diff, diffTruncated) = EditDiffBuilder.Build(
                relative,
                FileToolShared.NormalizeToLf(raw),
                FileToolShared.NormalizeToLf(updatedText),
                EditDiffBuilder.DefaultContextLines,
                MaxSuccessOutputChars - 320);

            // ---------- P2-1 dry_run：只预览不写盘 ----------
            if (dryRun)
            {
                var preview = new StringBuilder();
                preview.Append("DRY_RUN：未写盘（命中 ").Append(edits.Count).Append(" 处），下面是预览 diff：\n");
                if (diff.Length > 0)
                {
                    preview.AppendLine("```diff").Append(diff);
                    if (!diff.EndsWith('\n'))
                    {
                        preview.AppendLine();
                    }

                    preview.AppendLine("```");
                }

                preview.Append("hash_preview: ").AppendLine(newHashPreview);
                if (diffTruncated)
                {
                    preview.AppendLine("[truncated: true] diff 已按 2000 字符预算截断，完整区间请用 read_file(offset, limit) 查看。");
                }

                return Task.FromResult(ToolResult.Ok(
                    ClampOutput(preview.ToString(), MaxSuccessOutputChars),
                    durationMs: sw.ElapsedMilliseconds));
            }

            // ---------- P2-2：落盘前备份（阻塞式，无退路不覆盖） ----------
            string? backupShown = null;
            try
            {
                var (bakPath, _, _) = BackupStore.WriteBackup(fullPath, context.DataRoot, context.WorkspaceId, context.SessionId, "edit_file");
                if (bakPath != null)
                {
                    backupShown = GetBackupDisplay(context.DataRoot, bakPath);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"备份失败，写入已中止以防无退路覆盖：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

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
            var sb = new StringBuilder();
            if (replaceAll && matchCount >= MaxBulkWarnThreshold)
            {
                sb.Append("WARN_BULK_EDITS：本次替换 ").Append(matchCount).Append(" 处，请核对 diff。\n");
            }

            sb.Append("已修改 '").Append(relative).Append("'（替换 ").Append(edits.Count).Append(" 处；Encoding: ")
              .Append(detected.EncodingLabel).Append(", LineEndings: ").Append(detected.NewLineLabel)
              .Append(", match_mode: ").Append(matchMode)
              .Append(", hash: ").Append(newHashPreview);
            if (backupShown != null)
            {
                sb.Append(", backup: ").Append(backupShown);
            }

            sb.AppendLine("）");
            if (matchMode == "regex" && !regexExpandGroups && newString.Contains('$'))
            {
                sb.AppendLine("LITERAL_DOLLAR_NOTE：new_string 含 $ 已按字面写回（如需 $1 展开请传 regex_expand_groups:true）。");
            }

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

            var ok = ToolResult.Ok(
                ClampOutput(sb.ToString(), MaxSuccessOutputChars),
                durationMs: sw.ElapsedMilliseconds);
            try
            {
                ok.FileBytes = new FileInfo(fullPath).Length;
            }
            catch
            {
            }

            return Task.FromResult(ok);
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

     internal static ToolResult BuildAmbiguousResult(
         string relative, string raw, string oldString,
         FileToolShared.DetectedText detected, string matchMode,
         EditMatchEngine.MatchOptions options, int matchCount, long durationMs)
     {
         List<EditMatchEngine.NearestCandidate> nearest = EditMatchEngine.FindNearest(raw, oldString, options, 3);
         string lines = nearest.Count > 0
             ? string.Join(", ", nearest.Select(n => n.Line))
             : "（无相近行）";
         string payload = JsonSerializer.Serialize(new
         {
             ok = false,
             code = "ANCHOR_AMBIGUOUS",
             file = relative,
             matched = matchCount,
             nearest = nearest.Select(n => new { line = n.Line, score = Math.Round(n.Score, 2), preview = n.Preview }),
         });
         return ToolResult.FailCode("ANCHOR_AMBIGUOUS",
             $"ANCHOR_AMBIGUOUS：old_string 命中 {matchCount} 处 matched {matchCount} times（归一化后相同）。已拒绝单处替换以防改错位置。"
             + $"候选行：[{lines}]（见 nearest）。"
             + "请加长锚点至包含上下函数名/类名后重试，或改用 edit_lines(offset,limit) 指定行。",
             payload, durationMs: durationMs);
     }

    private static string GetBackupDisplay(string dataRoot, string bakAbsPath)
    {
        try
        {
            string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : Path.GetFullPath(dataRoot);
            string rel = Path.GetRelativePath(root, bakAbsPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal))
            {
                return rel;
            }
        }
        catch
        {
        }

        return bakAbsPath;
    }

    private static string NormalizeHash(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("sha256:".Length).Trim();
        }

        return trimmed;
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
