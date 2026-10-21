using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: edit_file 匹配/落盘/回吐（原 ExecuteAsync 后半 1:1 搬迁）。</summary>
internal sealed record EditFileRequest(
    string OldString,
    string NewString,
    bool ReplaceAll,
    string MatchMode,
    EditMatchEngine.MatchOptions Options,
    string Raw,
    string FullPath,
    string WorkspaceRoot,
    string Relative,
    bool DryRun,
    bool RegexExpandGroups,
    FileToolShared.DetectedText Detected
);

internal static class EditFileApplier
{
    internal static Task<Host.Desktop.Tools.ToolResult> ApplyAsync(
        EditFileRequest req,
        Host.Desktop.Tools.ToolContext context,
        System.Diagnostics.Stopwatch sw
    )
    {
        try
        {
            // ---------- 匹配 ----------
            var edits = new List<(int Start, int End, string Text)>();
            int matchCount;

            if (req.MatchMode == "regex")
            {
                Regex regex;
                try
                {
                    regex = new Regex(
                        req.OldString,
                        RegexOptions.Multiline | RegexOptions.CultureInvariant
                    );
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(
                        ToolResult.Fail(
                            $"regex 模式下的 old_string 不是合法的 .NET 正则：{ex.Message}。请修正模式或改用 match_mode=normalized。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                var regexNorm = EditMatchEngine.Normalize(
                    req.Raw,
                    new EditMatchEngine.MatchOptions
                    {
                        NormalizeEol = req.Options.NormalizeEol,
                        ExpandTabs = 0,
                        TrimTrailingSpace = false,
                        IgnoreLeadingWhitespace = false,
                    }
                );

                MatchCollection matches = regex.Matches(regexNorm.Text);
                matchCount = matches.Count;
                if (matchCount == 0)
                {
                    string diag = EditFileHelpers.BuildNotFoundDiagnostic(
                        req.Relative,
                        req.Raw,
                        req.OldString,
                        req.Detected,
                        req.MatchMode,
                        req.Options
                    );
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "OLD_STRING_NOT_FOUND",
                            "OLD_STRING_NOT_FOUND：" + diag,
                            diag,
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                if (!req.ReplaceAll && matchCount > 1)
                {
                    return Task.FromResult(
                        BuildAmbiguousResult(req, matchCount, sw.ElapsedMilliseconds)
                    );
                }

                foreach (Match m in matches)
                {
                    if (!req.ReplaceAll && edits.Count >= 1)
                    {
                        break;
                    }

                    if (m.Length == 0)
                    {
                        return Task.FromResult(
                            ToolResult.FailCode(
                                "REGEX_ZERO_LENGTH",
                                "REGEX_ZERO_LENGTH：regex 命中为空长度（模式不含实际文本），edit_file 无法定位替换区间。请在模式中包含待替换的实际内容。",
                                durationMs: sw.ElapsedMilliseconds
                            )
                        );
                    }

                    int rawStart = regexNorm.RawStart[m.Index];
                    int rawEnd = regexNorm.RawEnd[m.Index + m.Length - 1];
                    string replacement = req.RegexExpandGroups
                        ? m.Result(req.NewString)
                        : req.NewString;
                    edits.Add((rawStart, rawEnd, replacement));
                }
            }
            else
            {
                var haystack = EditMatchEngine.Normalize(req.Raw, req.Options);
                string needle = EditMatchEngine.Normalize(req.OldString, req.Options).Text;
                if (needle.Length == 0)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "ANCHOR_TOO_SHORT",
                            "ANCHOR_TOO_SHORT：old_string 归一化后为空（仅含空白或换行），无法定位替换区间。请提供包含实际文本的锚点。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                List<EditMatchEngine.MatchSpan> matches = EditMatchEngine.FindAll(
                    haystack.Text,
                    needle
                );
                matchCount = matches.Count;
                if (matchCount == 0)
                {
                    string diag = EditFileHelpers.BuildNotFoundDiagnostic(
                        req.Relative,
                        req.Raw,
                        req.OldString,
                        req.Detected,
                        req.MatchMode,
                        req.Options
                    );
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "OLD_STRING_NOT_FOUND",
                            "OLD_STRING_NOT_FOUND：" + diag,
                            diag,
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                if (matchCount > 1 && !req.ReplaceAll)
                {
                    return Task.FromResult(
                        BuildAmbiguousResult(req, matchCount, sw.ElapsedMilliseconds)
                    );
                }

                IEnumerable<EditMatchEngine.MatchSpan> chosen = req.ReplaceAll
                    ? matches
                    : matches.Take(1);
                foreach (EditMatchEngine.MatchSpan span in chosen)
                {
                    var (start, end) = EditMatchEngine.ToRawSpan(haystack, span);
                    edits.Add((start, end, req.NewString));
                }
            }

            if (edits.Count == 0)
            {
                string diag = EditFileHelpers.BuildNotFoundDiagnostic(
                    req.Relative,
                    req.Raw,
                    req.OldString,
                    req.Detected,
                    req.MatchMode,
                    req.Options
                );
                return Task.FromResult(
                    ToolResult.FailCode(
                        "OLD_STRING_NOT_FOUND",
                        "OLD_STRING_NOT_FOUND：" + diag,
                        diag,
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            // ---------- 只替换目标区：文件其余字节（换行/缩进风格）保持原样 ----------
            var updated = new StringBuilder(req.Raw);
            foreach (
                (int start, int end, string replacement) in edits.OrderByDescending(e => e.Start)
            )
            {
                string eol =
                    EditFileHelpers.DetectSpanEol(req.Raw, start, end) ?? req.Detected.NewLine;
                string text = FileToolShared.ConvertNewLines(replacement, eol);
                updated.Remove(start, end - start).Insert(start, text);
            }

            string updatedText = updated.ToString();
            string newHashPreview = FileToolShared.ComputeTextHash(updatedText, req.Detected);

            var (diff, diffTruncated) = EditDiffBuilder.Build(
                req.Relative,
                FileToolShared.NormalizeToLf(req.Raw),
                FileToolShared.NormalizeToLf(updatedText),
                EditDiffBuilder.DefaultContextLines,
                EditFileHelpers.MaxSuccessOutputChars - 320
            );

            // ---------- P2-1 dry_run：只预览不写盘 ----------
            if (req.DryRun)
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
                    preview.AppendLine(
                        "[truncated: true] diff 已按 2000 字符预算截断，完整区间请用 read_file(offset, limit) 查看。"
                    );
                }

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

            // ---------- P2-2：落盘前备份（阻塞式，无退路不覆盖） ----------
            string? backupShown = null;
            try
            {
                var (bakPath, _, _) = BackupStore.WriteBackup(
                    req.FullPath,
                    context.DataRoot,
                    context.WorkspaceId,
                    context.SessionId,
                    "edit_file"
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
                    req.FullPath,
                    updatedText,
                    req.Detected.Encoding,
                    req.Detected.Preamble
                );
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail($"文件写回失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
                );
            }

            // ---------- R4：成功回吐截断 ----------
            var sb = new StringBuilder();
            if (req.ReplaceAll && matchCount >= EditFileHelpers.MaxBulkWarnThreshold)
            {
                sb.Append("WARN_BULK_EDITS：本次替换 ").Append(matchCount).Append(" 处，请核对 diff。\n");
            }

            sb.Append("已修改 '")
                .Append(req.Relative)
                .Append("'（替换 ")
                .Append(edits.Count)
                .Append(" 处；Encoding: ")
                .Append(req.Detected.EncodingLabel)
                .Append(", LineEndings: ")
                .Append(req.Detected.NewLineLabel)
                .Append(", match_mode: ")
                .Append(req.MatchMode)
                .Append(", hash: ")
                .Append(newHashPreview);
            if (backupShown != null)
            {
                sb.Append(", backup: ").Append(backupShown);
            }

            sb.AppendLine("）");
            if (req.MatchMode == "regex" && !req.RegexExpandGroups && req.NewString.Contains('$'))
            {
                sb.AppendLine(
                    "LITERAL_DOLLAR_NOTE：new_string 含 $ 已按字面写回（如需 $1 展开请传 regex_expand_groups:true）。"
                );
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
                sb.AppendLine(
                    "[truncated: true] diff 已按 2000 字符预算截断，完整区间请用 read_file(offset, limit) 查看。"
                );
            }

            var ok = ToolResult.Ok(
                EditFileHelpers.ClampOutput(sb.ToString(), EditFileHelpers.MaxSuccessOutputChars),
                durationMs: sw.ElapsedMilliseconds
            );
            try
            {
                ok.FileBytes = new FileInfo(req.FullPath).Length;
            }
            catch { }

            return Task.FromResult(ok);
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

    internal static ToolResult BuildAmbiguousResult(
        EditFileRequest req,
        int matchCount,
        long durationMs
    )
    {
        List<EditMatchEngine.NearestCandidate> nearest = EditMatchEngine.FindNearest(
            req.Raw,
            req.OldString,
            req.Options,
            3
        );
        string lines =
            nearest.Count > 0 ? string.Join(", ", nearest.Select(n => n.Line)) : "（无相近行）";
        string payload = JsonSerializer.Serialize(
            new
            {
                ok = false,
                code = "ANCHOR_AMBIGUOUS",
                file = req.Relative,
                matched = matchCount,
                nearest = nearest.Select(n => new
                {
                    line = n.Line,
                    score = Math.Round(n.Score, 2),
                    preview = n.Preview
                }),
            }
        );
        return ToolResult.FailCode(
            "ANCHOR_AMBIGUOUS",
            $"ANCHOR_AMBIGUOUS：old_string 命中 {matchCount} 处 matched {matchCount} times（归一化后相同）。已拒绝单处替换以防改错位置。"
                + $"候选行：[{lines}]（见 nearest）。"
                + "请加长锚点至包含上下函数名/类名后重试，或改用 edit_lines(offset,limit) 指定行。",
            payload,
            durationMs: durationMs
        );
    }
}
