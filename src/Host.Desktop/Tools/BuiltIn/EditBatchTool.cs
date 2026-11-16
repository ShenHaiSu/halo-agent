using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

[HaloAgent.Tools.Tool("edit_batch")]
public sealed class EditBatchTool : IToolHandler
{
    internal const int MaxEdits = 8;
    internal const int MaxNewTextBytes = 256_000;

    public string Name => "edit_batch";

    public string Description =>
        "Apply multiple edits to one file atomically (all-or-nothing, single backup). "
        + "mode=lines edits {offset,limit,new_text} by 1-based line numbers; "
        + "mode=text edits {old_string,new_string} anchors matched simultaneously. "
        + "Ranges must be disjoint. "
        + "Example: {\"file_path\":\"src/a.ts\",\"mode\":\"lines\",\"edits\":[{\"offset\":120,\"limit\":3,\"new_text\":\"...\"}]}";

    public static string PromptBrief =>
        "Batch multi-range edit in one atomic call (disjoint ranges)";

    public static string[] PromptRules =>
        new[]
        {
            "edit_batch ranges must be disjoint: overlapping [offset,offset+limit) rejects the whole call.",
            "For large edit_batch calls, preview with dry_run:true first.",
        };

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString("file_path", "Target file path.", required: true)
            .AddString(
                "mode",
                "Edit addressing: lines (1-based ranges) or text (anchors). Default lines.",
                enumValues: new[] { "lines", "text" },
                defaultValue: "lines"
            )
            .AddArray(
                "edits",
                "Edit items. lines: {offset,limit,new_text}; text: {old_string,new_string}. Max 8 items.",
                required: true
            )
            .AddString(
                "match_mode",
                "text mode only: exact | normalized (default) | regex.",
                enumValues: new[] { "exact", "normalized", "regex" },
                defaultValue: "normalized"
            )
            .AddBoolean(
                "regex_expand_groups",
                "text+regex only: expand $1 in new_string. Default false."
            )
            .AddString(
                "expected_hash",
                "Optional sha256 of the file bytes; mismatch rejects the batch."
            )
            .AddBoolean("allow_empty", "Required true to write an empty new_text/new_string.")
            .AddBoolean("dry_run", "If true, only preview without writing.", defaultValue: false)
            .Build();
        return new(Name, Description, schema);
    }

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string mode = "lines";
            string? expectedHash = null;
            bool allowEmpty = false;
            bool dryRun = false;
            string matchMode = "normalized";
            bool regexExpandGroups = false;
            JsonElement editsEl = default;
            bool hasEdits = false;

            try
            {
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    filePathArg = args.GetString("file_path");
                    string? m = args.GetString("mode");
                    if (!string.IsNullOrWhiteSpace(m))
                    {
                        mode = m.Trim().ToLowerInvariant();
                    }

                    expectedHash = args.GetString("expected_hash");
                    if (args.TryGetBool("allow_empty", out bool ae))
                    {
                        allowEmpty = ae;
                    }

                    if (args.TryGetBool("dry_run", out bool dry))
                    {
                        dryRun = dry;
                    }

                    string? mm = args.GetString("match_mode");
                    if (!string.IsNullOrWhiteSpace(mm))
                    {
                        matchMode = mm.Trim().ToLowerInvariant();
                    }

                    if (args.TryGetBool("regex_expand_groups", out bool rx))
                    {
                        regexExpandGroups = rx;
                    }

                    if (root.TryGetProperty("edits", out JsonElement e))
                    {
                        editsEl = e.Clone();
                        hasEdits = true;
                    }
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/mode/edits 字段后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(
                    ToolResult.Fail("缺少必需参数 'file_path'。", durationMs: sw.ElapsedMilliseconds)
                );
            }

            if (mode is not ("lines" or "text"))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"mode 取值非法：'{mode}'。可选值：lines | text。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (matchMode is not ("exact" or "normalized" or "regex"))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"match_mode 取值非法：'{matchMode}'。可选值：exact | normalized | regex。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!hasEdits || editsEl.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "EMPTY_EDITS",
                        "EMPTY_EDITS：缺少必需参数 'edits'（非空数组）。请传入至少一项编辑后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            int editCount = editsEl.GetArrayLength();
            if (editCount == 0)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "EMPTY_EDITS",
                        "EMPTY_EDITS：edits 为空数组。请传入至少一项编辑后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (editCount > MaxEdits)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "TOO_MANY_EDITS",
                        $"TOO_MANY_EDITS：edits 共 {editCount} 项，超过上限 {MaxEdits}。请拆单后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            var (allowed, error, fullPath, _) = PathSafetyValidator.ValidateWrite(
                workspaceRoot,
                filePathArg
            );
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(error!, durationMs: sw.ElapsedMilliseconds));
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"文件不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。edit_batch 仅修改已存在文件，新建请用 write_file。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (FileToolShared.LooksBinary(fullPath))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"拒绝编辑二进制文件：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            FileToolShared.DetectedText detected;
            try
            {
                detected = FileToolShared.ReadAllTextWithDetection(fullPath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail($"文件读取失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
                );
            }

            string raw = detected.Content;
            string relative = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);

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

                string want = EditFileHelpers.NormalizeHash(expectedHash);
                if (!string.Equals(want, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "EXPECTED_HASH_MISMATCH",
                            $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前文件 hash: {FileToolShared.TruncateHash(currentHash)}，完整 hash 请用 read_file 查看；期望: {want}）。请用 read_file 重新确认内容后重试，或不传 expected_hash。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }

            if (mode == "lines")
            {
                return ExecuteLinesBatch(
                    editsEl,
                    raw,
                    relative,
                    fullPath,
                    workspaceRoot,
                    detected,
                    allowEmpty,
                    dryRun,
                    context,
                    sw
                );
            }

            return ExecuteTextBatch(
                editsEl,
                raw,
                relative,
                fullPath,
                workspaceRoot,
                detected,
                matchMode,
                regexExpandGroups,
                allowEmpty,
                dryRun,
                context,
                sw
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"edit_batch 执行异常：{ex.Message}。请修正参数后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }
        finally
        {
            sw.Stop();
        }
    }

    private static Task<ToolResult> ExecuteLinesBatch(
        JsonElement editsEl,
        string raw,
        string relative,
        string fullPath,
        string workspaceRoot,
        FileToolShared.DetectedText detected,
        bool allowEmpty,
        bool dryRun,
        ToolContext context,
        Stopwatch sw
    )
    {
        var items = new List<LinesEdit>();
        long totalNewBytes = 0;
        int idx = 0;
        foreach (JsonElement item in editsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "TYPE_MISMATCH",
                        $"TYPE_MISMATCH：edits[{idx}] 应为对象。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            int offVal = 0;
            int limVal = 0;
            bool hasOffset =
                item.TryGetProperty("offset", out JsonElement offEl)
                && offEl.ValueKind == JsonValueKind.Number
                && offEl.TryGetInt32(out offVal);
            bool hasLimit =
                item.TryGetProperty("limit", out JsonElement limEl)
                && limEl.ValueKind == JsonValueKind.Number
                && limEl.TryGetInt32(out limVal);
            bool hasText =
                item.TryGetProperty("new_text", out JsonElement textEl)
                && textEl.ValueKind == JsonValueKind.String;

            if (!hasOffset)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "OUT_OF_RANGE",
                        $"OUT_OF_RANGE：edits[{idx}] 缺少合法 'offset'（1-based 行号）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!hasLimit)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "MISSING_LIMIT",
                        $"MISSING_LIMIT：edits[{idx}] 缺少必需参数 'limit'（替换 N 行传 N，纯插入传 0）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string newText = hasText ? textEl.GetString() ?? string.Empty : string.Empty;
            if (!hasText && !allowEmpty)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "MISSING_REQUIRED_PARAM",
                        $"MISSING_REQUIRED_PARAM：edits[{idx}] 缺少必需参数 'new_text'。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (newText.Length == 0 && !allowEmpty)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "EMPTY_NEW_TEXT",
                        $"EMPTY_NEW_TEXT：edits[{idx}] 的 new_text 为空会整段删除目标行，本次已拒绝。确认要删除请显式传 allow_empty:true。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            totalNewBytes += Encoding.UTF8.GetByteCount(newText);
            items.Add(new LinesEdit(offVal, limVal, newText));
            idx++;
        }

        if (totalNewBytes > MaxNewTextBytes)
        {
            return Task.FromResult(
                ToolResult.FailCode(
                    "CONTENT_TOO_LARGE",
                    $"CONTENT_TOO_LARGE：edits 内 new_text 总字节 {totalNewBytes} 超过上限 {MaxNewTextBytes}。请拆单后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        List<EditLinesTool.RawLine> lines = EditLinesTool.SplitRawLines(raw);
        int totalLines = lines.Count;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Offset < 1 || it.Limit < 0)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "OUT_OF_RANGE",
                        $"OUT_OF_RANGE：edits[{i}] offset={it.Offset} limit={it.Limit} 非法（offset≥1，limit≥0）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (it.Offset > totalLines + 1)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "OUT_OF_RANGE",
                        $"OUT_OF_RANGE：edits[{i}] offset={it.Offset} 超出文件末行（共 {totalLines} 行）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                if (RangesOverlap(items[i].Offset, items[i].Limit, items[j].Offset, items[j].Limit))
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "EDITS_OVERLAP",
                            $"EDITS_OVERLAP：edits[{i}] 与 edits[{j}] 区间重叠，整单已拒绝。区间须互斥（[offset,offset+limit) 不交叠；limit=0 同点双插入亦判重叠）。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }
        }

        var order = items.Select((it, i) => (it, i)).OrderByDescending(t => t.it.Offset).ToList();
        string updated = raw;
        foreach (var (it, _) in order)
        {
            updated = ApplySingleLineEdit(updated, detected, it.Offset, it.Limit, it.NewText);
        }

        if (string.Equals(updated, raw, StringComparison.Ordinal))
        {
            return Task.FromResult(
                ToolResult.Fail(
                    "NO_CHANGE：批量编辑后内容与现有完全一致，未做写入。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        long finalBytes = detected.Encoding.GetByteCount(updated) + detected.Preamble.Length;
        if (finalBytes > WriteFileTool.HardMaxWriteBytes)
        {
            return Task.FromResult(
                ToolResult.FailCode(
                    "CONTENT_TOO_LARGE",
                    $"CONTENT_TOO_LARGE：批量写回 {finalBytes} 字节超过硬上限 {WriteFileTool.HardMaxWriteBytes}。请拆单。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        string newHash = FileToolShared.ComputeTextHash(updated, detected);
        var spans = items
            .OrderBy(t => t.Offset)
            .Select(t => new EditDiffBuilder.BatchSpan(
                t.Offset,
                t.Limit,
                FileToolShared.NormalizeToLf(t.NewText).Length == 0
                    ? 0
                    : FileToolShared.NormalizeToLf(t.NewText).Split('\n').Length
            ))
            .ToList();
        var (diff, diffTruncated) = EditDiffBuilder.BuildMulti(
            relative,
            FileToolShared.NormalizeToLf(raw),
            FileToolShared.NormalizeToLf(updated),
            spans
        );

        if (dryRun)
        {
            var preview = new StringBuilder();
            preview.Append("DRY_RUN：行号批量预览（未写盘），共 ").Append(items.Count).AppendLine(" 处");
            for (int i = 0; i < items.Count; i++)
            {
                preview.Append('[').Append(i).Append("] line ").Append(items[i].Offset);
                if (items[i].Limit == 0)
                {
                    preview.Append(" insert@").Append(items[i].Offset);
                }
                else
                {
                    preview.Append("..").Append(items[i].Offset + items[i].Limit - 1);
                }

                preview.Append(" → ").AppendLine(SummarizeNewText(items[i].NewText));
            }

            if (diff.Length > 0)
            {
                preview.AppendLine("```diff").Append(diff);
                if (!diff.EndsWith('\n'))
                {
                    preview.AppendLine();
                }

                preview.AppendLine("```");
            }

            preview.Append("hash_preview: ").AppendLine(newHash);
            return Task.FromResult(
                ToolResult.Ok(
                    EditFileHelpers.ClampOutput(
                        preview.ToString(),
                        EditFileHelpers.MaxSuccessOutputChars
                    ),
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        string? backupShown = null;
        try
        {
            var (bakPath, _, _) = BackupStore.WriteBackup(
                fullPath,
                context.DataRoot,
                context.WorkspaceId,
                context.SessionId,
                "edit_batch"
            );
            if (bakPath != null)
            {
                backupShown = EditFileHelpers.GetBackupDisplay(context.DataRoot, bakPath);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"备份失败，写入已中止以防无退路覆盖：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        try
        {
            FileToolShared.AtomicWriteAllText(
                fullPath,
                updated,
                detected.Encoding,
                detected.Preamble
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail($"文件写回失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
            );
        }

        var sb = new StringBuilder();
        sb.Append("已按行号批量修改 '")
            .Append(relative)
            .Append("'（")
            .Append(items.Count)
            .Append(" 处；Encoding: ")
            .Append(detected.EncodingLabel)
            .Append(", LineEndings: ")
            .Append(detected.NewLineLabel)
            .Append(", hash: ")
            .Append(newHash);
        if (backupShown != null)
        {
            sb.Append(", backup: ").Append(backupShown);
        }

        sb.AppendLine("）");
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append('[').Append(i).Append("] ");
            if (items[i].Limit == 0)
            {
                sb.Append("insert@").Append(items[i].Offset);
            }
            else
            {
                sb.Append("line ")
                    .Append(items[i].Offset)
                    .Append("..")
                    .Append(items[i].Offset + items[i].Limit - 1);
            }

            sb.Append(" → ").AppendLine(SummarizeNewText(items[i].NewText));
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
            sb.AppendLine("[truncated: true] diff 已按预算截断，完整区间请用 read_file(offset, limit) 查看。");
        }

        var ok = ToolResult.Ok(
            EditFileHelpers.ClampOutput(sb.ToString(), EditFileHelpers.MaxSuccessOutputChars),
            durationMs: sw.ElapsedMilliseconds
        );
        try
        {
            ok.FileBytes = new FileInfo(fullPath).Length;
        }
        catch { }

        return Task.FromResult(ok);
    }

    private static Task<ToolResult> ExecuteTextBatch(
        JsonElement editsEl,
        string raw,
        string relative,
        string fullPath,
        string workspaceRoot,
        FileToolShared.DetectedText detected,
        string matchMode,
        bool regexExpandGroups,
        bool allowEmpty,
        bool dryRun,
        ToolContext context,
        Stopwatch sw
    )
    {
        var anchors = new List<TextEdit>();
        long totalNewBytes = 0;
        int idx = 0;
        foreach (JsonElement item in editsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "TYPE_MISMATCH",
                        $"TYPE_MISMATCH：edits[{idx}] 应为对象。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            bool hasOld =
                item.TryGetProperty("old_string", out JsonElement oldEl)
                && oldEl.ValueKind == JsonValueKind.String;
            bool hasNew =
                item.TryGetProperty("new_string", out JsonElement newEl)
                && newEl.ValueKind == JsonValueKind.String;

            if (!hasOld)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "MISSING_REQUIRED_PARAM",
                        $"MISSING_REQUIRED_PARAM：edits[{idx}] 缺少必需参数 'old_string'。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!hasNew)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "MISSING_REQUIRED_PARAM",
                        $"MISSING_REQUIRED_PARAM：edits[{idx}] 缺少必需参数 'new_string'。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string oldString = oldEl.GetString() ?? string.Empty;
            string newString = newEl.GetString() ?? string.Empty;
            if (newString.Length == 0 && !allowEmpty)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "EMPTY_NEW_TEXT",
                        $"EMPTY_NEW_TEXT：edits[{idx}] 的 new_string 为空，确认删除请显式传 allow_empty:true。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            totalNewBytes += Encoding.UTF8.GetByteCount(newString);
            anchors.Add(new TextEdit(oldString, newString));
            idx++;
        }

        if (totalNewBytes > MaxNewTextBytes)
        {
            return Task.FromResult(
                ToolResult.FailCode(
                    "CONTENT_TOO_LARGE",
                    $"CONTENT_TOO_LARGE：edits 内 new_string 总字节 {totalNewBytes} 超过上限 {MaxNewTextBytes}。请拆单后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        var options =
            matchMode == "exact"
                ? new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = true,
                    ExpandTabs = 0,
                    TrimTrailingSpace = false,
                    IgnoreLeadingWhitespace = false,
                }
                : new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = true,
                    ExpandTabs = 4,
                    TrimTrailingSpace = true,
                    IgnoreLeadingWhitespace = false,
                };

        if (matchMode != "regex")
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                string probe = EditMatchEngine.Normalize(anchors[i].OldString, options).Text;
                string compact = Regex.Replace(probe, @"\s+", string.Empty);
                if (probe.Length == 0 || compact.Length < EditFileHelpers.MinAnchorNonBlankChars)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_TOO_SHORT",
                            $"ANCHOR_TOO_SHORT：edits[{i}] 锚点过短（去空白后仅 {compact.Length} 个字符，需 ≥ {EditFileHelpers.MinAnchorNonBlankChars}）。请加长锚点或改用 mode=lines。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }
        }

        var spans = new List<(int Start, int End, int Index)>();
        if (matchMode == "regex")
        {
            var norm = EditMatchEngine.Normalize(
                raw,
                new EditMatchEngine.MatchOptions
                {
                    NormalizeEol = options.NormalizeEol,
                    ExpandTabs = 0,
                    TrimTrailingSpace = false,
                    IgnoreLeadingWhitespace = false,
                }
            );
            for (int i = 0; i < anchors.Count; i++)
            {
                Regex regex;
                try
                {
                    regex = new Regex(
                        anchors[i].OldString,
                        RegexOptions.Multiline | RegexOptions.CultureInvariant
                    );
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(
                        ToolResult.Fail(
                            $"edits[{i}] regex 非法：{ex.Message}。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                MatchCollection matches = regex.Matches(norm.Text);
                if (matches.Count == 0)
                {
                    var nearest = EditMatchEngine.FindNearest(
                        raw,
                        anchors[i].OldString,
                        options,
                        3
                    );
                    string diag = EditFileHelpers.BuildNotFoundDiagnostic(
                        relative,
                        raw,
                        anchors[i].OldString,
                        detected,
                        matchMode,
                        options
                    );
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "OLD_STRING_NOT_FOUND",
                            $"OLD_STRING_NOT_FOUND：edits[{i}] 未命中。" + diag,
                            diag,
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                if (matches.Count > 1)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_AMBIGUOUS",
                            $"ANCHOR_AMBIGUOUS：edits[{i}] 命中 {matches.Count} 处，batch 内禁用多命中，整单已拒绝。请加长锚点或改用 mode=lines。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                Match m = matches[0];
                if (m.Length == 0)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "REGEX_ZERO_LENGTH",
                            $"REGEX_ZERO_LENGTH：edits[{i}] 正则命中空长度，无法定位替换区间。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                int rawStart = norm.RawStart[m.Index];
                int rawEnd = norm.RawEnd[m.Index + m.Length - 1];
                spans.Add((rawStart, rawEnd, i));
            }
        }
        else
        {
            var norm = EditMatchEngine.Normalize(raw, options);
            for (int i = 0; i < anchors.Count; i++)
            {
                string needle = EditMatchEngine.Normalize(anchors[i].OldString, options).Text;
                var hits = EditMatchEngine.FindAll(norm.Text, needle);
                if (hits.Count == 0)
                {
                    string diag = EditFileHelpers.BuildNotFoundDiagnostic(
                        relative,
                        raw,
                        anchors[i].OldString,
                        detected,
                        matchMode,
                        options
                    );
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "OLD_STRING_NOT_FOUND",
                            $"OLD_STRING_NOT_FOUND：edits[{i}] 未命中。" + diag,
                            diag,
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                if (hits.Count > 1)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_AMBIGUOUS",
                            $"ANCHOR_AMBIGUOUS：edits[{i}] 命中 {hits.Count} 处，batch 内禁用多命中，整单已拒绝。请加长锚点或改用 mode=lines。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                var (s, e) = EditMatchEngine.ToRawSpan(norm, hits[0]);
                spans.Add((s, e, i));
            }
        }

        var ordered = spans.OrderBy(t => t.Start).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "EDITS_OVERLAP",
                        $"EDITS_OVERLAP：edits[{ordered[i - 1].Index}] 与 edits[{ordered[i].Index}] 命中区间重叠，整单已拒绝。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }
        }

        string updated = raw;
        foreach (var (s, e, i) in spans.OrderByDescending(t => t.Start))
        {
            string replacement = anchors[i].NewString;
            if (matchMode == "regex" && regexExpandGroups)
            {
                replacement = ExpandRegexGroups(
                    raw.Substring(s, e - s),
                    anchors[i].OldString,
                    replacement
                );
            }

            string? spanEol = EditFileHelpers.DetectSpanEol(raw, s, e);
            string fileEol = detected.NewLine;
            string targetEol = spanEol ?? fileEol;
            string converted = FileToolShared.ConvertNewLines(replacement, targetEol);
            updated = updated.Substring(0, s) + converted + updated.Substring(e);
        }

        if (string.Equals(updated, raw, StringComparison.Ordinal))
        {
            return Task.FromResult(
                ToolResult.Fail(
                    "NO_CHANGE：批量编辑后内容与现有完全一致，未做写入。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        long finalBytes = detected.Encoding.GetByteCount(updated) + detected.Preamble.Length;
        if (finalBytes > WriteFileTool.HardMaxWriteBytes)
        {
            return Task.FromResult(
                ToolResult.FailCode(
                    "CONTENT_TOO_LARGE",
                    $"CONTENT_TOO_LARGE：批量写回 {finalBytes} 字节超过硬上限 {WriteFileTool.HardMaxWriteBytes}。请拆单。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        string newHash = FileToolShared.ComputeTextHash(updated, detected);
        var diffSpans = spans
            .OrderBy(t => t.Start)
            .Select(t => new EditDiffBuilder.BatchSpan(LineOf(raw, t.Start), 1, 1))
            .ToList();
        var (diff, diffTruncated) = EditDiffBuilder.BuildMulti(
            relative,
            FileToolShared.NormalizeToLf(raw),
            FileToolShared.NormalizeToLf(updated),
            diffSpans
        );

        if (dryRun)
        {
            var preview = new StringBuilder();
            preview.Append("DRY_RUN：锚点批量预览（未写盘），共 ").Append(anchors.Count).AppendLine(" 处");
            for (int i = 0; i < anchors.Count; i++)
            {
                preview
                    .Append('[')
                    .Append(i)
                    .Append("] anchor ")
                    .Append(anchors[i].OldString.Length)
                    .Append(" chars → ")
                    .AppendLine(SummarizeNewText(anchors[i].NewString));
            }

            if (diff.Length > 0)
            {
                preview.AppendLine("```diff").Append(diff);
                if (!diff.EndsWith('\n'))
                {
                    preview.AppendLine();
                }

                preview.AppendLine("```");
            }

            preview.Append("hash_preview: ").AppendLine(newHash);
            return Task.FromResult(
                ToolResult.Ok(
                    EditFileHelpers.ClampOutput(
                        preview.ToString(),
                        EditFileHelpers.MaxSuccessOutputChars
                    ),
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        string? backupShown = null;
        try
        {
            var (bakPath, _, _) = BackupStore.WriteBackup(
                fullPath,
                context.DataRoot,
                context.WorkspaceId,
                context.SessionId,
                "edit_batch"
            );
            if (bakPath != null)
            {
                backupShown = EditFileHelpers.GetBackupDisplay(context.DataRoot, bakPath);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"备份失败，写入已中止以防无退路覆盖：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }

        try
        {
            FileToolShared.AtomicWriteAllText(
                fullPath,
                updated,
                detected.Encoding,
                detected.Preamble
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail($"文件写回失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
            );
        }

        var sb = new StringBuilder();
        sb.Append("已按锚点批量修改 '")
            .Append(relative)
            .Append("'（")
            .Append(anchors.Count)
            .Append(" 处；Encoding: ")
            .Append(detected.EncodingLabel)
            .Append(", LineEndings: ")
            .Append(detected.NewLineLabel)
            .Append(", hash: ")
            .Append(newHash);
        if (backupShown != null)
        {
            sb.Append(", backup: ").Append(backupShown);
        }

        sb.AppendLine("）");
        for (int i = 0; i < anchors.Count; i++)
        {
            sb.Append('[')
                .Append(i)
                .Append("] anchor ")
                .Append(anchors[i].OldString.Length)
                .Append(" chars → ")
                .AppendLine(SummarizeNewText(anchors[i].NewString));
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
            sb.AppendLine("[truncated: true] diff 已按预算截断，完整区间请用 read_file(offset, limit) 查看。");
        }

        var ok = ToolResult.Ok(
            EditFileHelpers.ClampOutput(sb.ToString(), EditFileHelpers.MaxSuccessOutputChars),
            durationMs: sw.ElapsedMilliseconds
        );
        try
        {
            ok.FileBytes = new FileInfo(fullPath).Length;
        }
        catch { }

        return Task.FromResult(ok);
    }

    private static bool RangesOverlap(int aOffset, int aLimit, int bOffset, int bLimit)
    {
        int aStart = aOffset;
        int aEnd = aOffset + Math.Max(0, aLimit);
        int bStart = bOffset;
        int bEnd = bOffset + Math.Max(0, bLimit);
        bool aEmpty = aLimit == 0;
        bool bEmpty = bLimit == 0;
        if (aEmpty && bEmpty)
        {
            return aStart == bStart;
        }

        if (aEmpty)
        {
            return bStart < aStart && aStart < bEnd;
        }

        if (bEmpty)
        {
            return aStart < bStart && bStart < aEnd;
        }

        return Math.Max(aStart, bStart) < Math.Min(aEnd, bEnd);
    }

    private static string ApplySingleLineEdit(
        string current,
        FileToolShared.DetectedText detected,
        int offset,
        int limit,
        string newText
    )
    {
        List<EditLinesTool.RawLine> lines = EditLinesTool.SplitRawLines(current);
        int totalLines = lines.Count;
        int startIdx = Math.Clamp(offset - 1, 0, totalLines);
        int endIdx = Math.Min(startIdx + Math.Max(0, limit), totalLines);
        string head = startIdx < totalLines ? current.Substring(0, lines[startIdx].Start) : current;
        string tail = endIdx < totalLines ? current.Substring(lines[endIdx].Start) : string.Empty;

        string? blockEol = null;
        if (endIdx - startIdx > 0 && endIdx - 1 < totalLines && lines[endIdx - 1].EolLength > 0)
        {
            var ln = lines[endIdx - 1];
            blockEol = current.Substring(ln.Start + ln.ContentLength, ln.EolLength);
        }
        else if (startIdx < totalLines && lines[startIdx].EolLength > 0)
        {
            var ln = lines[startIdx];
            blockEol = current.Substring(ln.Start + ln.ContentLength, ln.EolLength);
        }

        string eol = blockEol ?? detected.NewLine;
        bool needLeadingEol = startIdx >= totalLines && head.Length > 0 && !EndsWithEol(head);
        bool needTrailingEol;
        if (tail.Length > 0)
        {
            needTrailingEol = true;
        }
        else if (endIdx - startIdx > 0)
        {
            needTrailingEol = lines[endIdx - 1].EolLength > 0;
        }
        else
        {
            needTrailingEol = EndsWithEol(head);
        }

        string newTextLf = FileToolShared.NormalizeToLf(newText);
        bool newTextEndsWithEol = newTextLf.EndsWith('\n');
        string[] newLines = newTextLf.Length == 0 ? Array.Empty<string>() : newTextLf.Split('\n');
        int newLineCount = newLines.Length;
        if (newTextEndsWithEol && newLineCount > 0)
        {
            newLineCount--;
        }

        var rebuilt = new StringBuilder(current.Length + newTextLf.Length + 16);
        rebuilt.Append(head);
        if (needLeadingEol && newLineCount > 0)
        {
            rebuilt.Append(eol);
        }

        for (int i = 0; i < newLineCount; i++)
        {
            if (i > 0)
            {
                rebuilt.Append(eol);
            }

            rebuilt.Append(newLines[i]);
        }

        if (newLineCount > 0 && (needTrailingEol || newTextEndsWithEol))
        {
            rebuilt.Append(eol);
        }

        rebuilt.Append(tail);
        return rebuilt.ToString();
    }

    private static bool EndsWithEol(string text) =>
        text.Length > 0 && (text[text.Length - 1] == '\n' || text[text.Length - 1] == '\r');

    private static int LineOf(string raw, int charIndex)
    {
        int line = 1;
        for (int i = 0; i < charIndex && i < raw.Length; i++)
        {
            if (raw[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string SummarizeNewText(string text)
    {
        string lf = FileToolShared.NormalizeToLf(text);
        int lines = lf.Length == 0 ? 0 : lf.Split('\n').Length;
        if (lf.EndsWith('\n') && lines > 0)
        {
            lines--;
        }

        string oneLine = lf.Replace("\n", "\\n");
        if (oneLine.Length > 80)
        {
            oneLine = oneLine.Substring(0, 80) + "...";
        }

        return $"{lines} 行 \"{oneLine}\"";
    }

    private static string ExpandRegexGroups(string matched, string pattern, string replacement)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
            return regex.Replace(matched, replacement);
        }
        catch
        {
            return replacement;
        }
    }

    private sealed record LinesEdit(int Offset, int Limit, string NewText);

    private sealed record TextEdit(string OldString, string NewString);
}
