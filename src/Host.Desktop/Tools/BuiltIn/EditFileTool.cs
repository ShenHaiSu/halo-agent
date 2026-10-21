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
[HaloAgent.Tools.Tool("edit_file")]
public sealed class EditFileTool : IToolHandler
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public string Name => "edit_file";

    // NEED17 PromptBrief: Anchor-based partial edit
    // NEED17 PromptRules: old_string must be verbatim copy-paste from read_file; multiple anchors need multiple calls; merge adjacent edits.
    public string Description =>
        "Replace a text block in an existing file. Default match_mode=normalized tolerates CRLF/LF, Tab/space "
        + "and trailing-space differences while writing back in the file's own byte style. "
        + "A miss returns a structured diagnostic (nearest lines + whitespace visualization + next step). "
        + "For very large files prefer edit_lines. For regex mode, $ is literal by default. "
        + "For single-file small edits (multi-anchor in one call is not supported; use multiple calls or edit_lines). Large refactors use write_file. "
        + "Example: {\"file_path\":\"src/a.ts\",\"old_string\":\"  if (x) {\\n    foo();\\n  }\",\"new_string\":\"  if (x) {\\n    bar();\\n  }\"}";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString("file_path", "Target file path.", required: true)
            .AddString(
                "old_string",
                "Verbatim copy-paste from read_file (2-5 context lines for uniqueness). Min 16 chars.",
                required: true,
                minLength: 16
            )
            .AddString(
                "new_string",
                "Replacement text. $ is literal unless regex_expand_groups:true.",
                required: true
            )
            .AddBoolean(
                "replace_all",
                "If true, replace all occurrences. If false (default), old_string must match exactly once.",
                defaultValue: false
            )
            .AddString(
                "match_mode",
                "exact=strict (legacy behaviour); normalized (default)=tolerate EOL/Tab/trailing-space; regex=treat old_string as a .NET regex over the normalized text.",
                enumValues: new string[] { "normalized", "exact", "regex" },
                defaultValue: "normalized"
            )
            .AddBoolean(
                "regex_expand_groups",
                "Only for match_mode=regex. If true, expand $1/${name} in new_string. Default false (literal write, safe for $ in code)."
            )
            .AddString(
                "expected_hash",
                "Optional sha256 of the file bytes; mismatch rejects the edit to avoid concurrent overwrite."
            )
            .AddBoolean("dry_run", "If true, only compute and preview the diff without writing.")
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
                    expandTabs = Math.Clamp(parsedTabs, 0, EditFileHelpers.MaxExpandTabs);
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
                return Task.FromResult(
                    ToolResult.Fail(
                        $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/old_string/new_string 字段后重试。",
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

            if (string.IsNullOrEmpty(oldString))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        "缺少必需参数 'old_string'。请先用 read_file 查看文件精确内容后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (newString == null)
            {
                return Task.FromResult(
                    ToolResult.Fail("缺少必需参数 'new_string'。", durationMs: sw.ElapsedMilliseconds)
                );
            }

            if (matchMode is not ("exact" or "normalized" or "regex"))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"match_mode 取值非法：'{matchMode}'。可选值：exact | normalized | regex（默认 normalized）。",
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
                        $"文件不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。edit_file 仅修改已存在文件，新建请用 write_file。",
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

            var options =
                matchMode == "exact"
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
                if (
                    needleProbe.Length == 0
                    || compactProbe.Length < EditFileHelpers.MinAnchorNonBlankChars
                )
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_TOO_SHORT",
                            $"ANCHOR_TOO_SHORT：锚点过短（去空白后仅 {compactProbe.Length} 个字符，需 ≥ {EditFileHelpers.MinAnchorNonBlankChars}）。请加长锚点（含 2-5 行上下文）或改用 edit_lines(offset, limit) 精确按行编辑。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }

            // ---------- R5：高危锚点拦截（`},` + replace_all 污染 183 处的复盘点） ----------
            if (replaceAll && matchMode != "regex")
            {
                string compact = WhitespaceRun.Replace(oldString, string.Empty);
                if (compact.Length < EditFileHelpers.MinAnchorCharsForReplaceAll)
                {
                    var probe = EditMatchEngine.Normalize(raw, options);
                    int occurrences = EditMatchEngine
                        .FindAll(probe.Text, EditMatchEngine.Normalize(oldString, options).Text)
                        .Count;
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_TOO_SHORT",
                            $"ANCHOR_TOO_SHORT：锚点去空白后仅 {compact.Length} 个字符且 replace_all=true，本次已拒绝"
                                + $"（文件内命中 {occurrences} 处，若放行会批量污染）。"
                                + "请加长锚点（建议 ≥ 8 个非空白字符，含 2-5 行上下文）或改用 edit_lines(offset, limit) 精确按行编辑。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
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

                string want = EditFileHelpers.NormalizeHash(expectedHash);
                if (!string.Equals(want, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "EXPECTED_HASH_MISMATCH",
                            $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前文件 hash: {FileToolShared.TruncateHash(currentHash)}，完整 hash 请用 read_file 查看；期望: {want}）。"
                                + "请用 read_file 重新确认内容后重试，或不传 expected_hash。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }
            var req = new EditFileRequest(
                oldString!,
                newString,
                replaceAll,
                matchMode,
                options,
                raw,
                fullPath,
                workspaceRoot,
                relative,
                dryRun,
                regexExpandGroups,
                detected
            );
            return EditFileApplier.ApplyAsync(req, context, sw);
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"edit_file 执行异常：{ex.Message}。请修正参数后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }
        finally
        {
            sw.Stop();
        }
    }
}
