using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// edit_lines：need14 R2 逃生通道——按 1-based 行号区间整段替换，不做任何字符串匹配。
/// 天然免疫缩进 / Tab / 换行 / 超长单行问题，行号口径与 read_file 完全一致；
/// 支持 expected_hash 防并发覆盖（不符即拒绝），空 new_text 需显式 allow_empty=true。
/// </summary>
public sealed class EditLinesTool : IToolHandler
{
    /// <summary>R4：成功回吐上限（与 edit_file 一致）。</summary>
    internal const int MaxSuccessOutputChars = EditFileTool.MaxSuccessOutputChars;

    public string Name => "edit_lines";

    public string Description =>
        "Replace an exact 1-based line range (same numbering as read_file) without any string matching. " +
        "Immune to CRLF/Tab/indentation/long-line issues: use this for huge files or when edit_file cannot match. " +
        "The file's own EOL style and encoding are preserved.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Target file path.\"},"
        + "\"offset\":{\"type\":\"integer\",\"description\":\"1-based first line to replace (same numbering as read_file).\"},"
        + "\"limit\":{\"type\":\"integer\",\"description\":\"Number of lines to replace (0 = pure insertion before offset).\"},"
        + "\"new_text\":{\"type\":\"string\",\"description\":\"Full replacement text; LF newlines are converted to the file's own EOL when writing.\"},"
        + "\"expected_hash\":{\"type\":\"string\",\"description\":\"Optional sha256 of the file (or of the covered lines) taken from a previous result; mismatch rejects the edit.\"},"
        + "\"allow_empty\":{\"type\":\"boolean\",\"description\":\"Required true to write an empty new_text (line deletion) and avoid accidental mass deletion.\"}"
        + "},\"required\":[\"file_path\",\"offset\",\"new_text\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string? newText = null;
            string? expectedHash = null;
            int offset = 0;
            int limit = 0;
            bool hasLimit = false;
            bool allowEmpty = false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("file_path", out JsonElement fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    filePathArg = fpEl.GetString();
                }

                if (root.TryGetProperty("offset", out JsonElement offEl) && offEl.ValueKind == JsonValueKind.Number
                    && offEl.TryGetInt32(out int parsedOffset))
                {
                    offset = parsedOffset;
                }

                if (root.TryGetProperty("limit", out JsonElement limEl) && limEl.ValueKind == JsonValueKind.Number
                    && limEl.TryGetInt32(out int parsedLimit))
                {
                    limit = parsedLimit;
                    hasLimit = true;
                }

                if (root.TryGetProperty("new_text", out JsonElement txtEl) && txtEl.ValueKind == JsonValueKind.String)
                {
                    newText = txtEl.GetString();
                }

                if (root.TryGetProperty("expected_hash", out JsonElement hashEl) && hashEl.ValueKind == JsonValueKind.String)
                {
                    expectedHash = hashEl.GetString();
                }

                if (root.TryGetProperty("allow_empty", out JsonElement emptyEl) &&
                    (emptyEl.ValueKind == JsonValueKind.True || emptyEl.ValueKind == JsonValueKind.False))
                {
                    allowEmpty = emptyEl.GetBoolean();
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/offset/limit/new_text 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(ToolResult.Fail("缺少必需参数 'file_path'。", durationMs: sw.ElapsedMilliseconds));
            }

            if (offset < 1)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"offset 必须是 1-based 行号（≥1），收到 {offset}。请先用 read_file 确认目标行号。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (!hasLimit)
            {
                limit = 1;
            }

            if (limit < 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"limit 不能为负（收到 {limit}）。0 表示在 offset 前纯插入。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (newText == null)
            {
                if (!allowEmpty)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "缺少必需参数 'new_text'。若确实要删除该行区间，请显式传 new_text=\"\" 且 allow_empty=true。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                newText = string.Empty;
            }

            if (newText.Length == 0 && !allowEmpty)
            {
                return Task.FromResult(ToolResult.Fail(
                    "EMPTY_NEW_TEXT：new_text 为空会整段删除目标行，本次已拒绝。确认要删除请显式传 allow_empty=true。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            var (allowed, error, fullPath, _) = PathSafetyValidator.ValidateWrite(workspaceRoot, filePathArg);
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(error!, durationMs: sw.ElapsedMilliseconds));
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件不存在：'{fullPath}'。edit_lines 仅修改已存在文件，新建请用 write_file。",
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
                return Task.FromResult(ToolResult.Fail($"文件读取失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds));
            }

            string raw = detected.Content;
            string relative = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);
            List<RawLine> lines = SplitRawLines(raw);
            int totalLines = lines.Count;

            // ---------- expected_hash：防并发覆盖 ----------
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                string currentFileHash = HashFileBytes(fullPath, detected);
                int hashStart = Math.Clamp(offset - 1, 0, Math.Max(0, totalLines - 1));
                int hashEnd = Math.Min(hashStart + Math.Max(limit, 1), totalLines);
                string covered = hashEnd > hashStart
                    ? string.Join("\n", Enumerable.Range(hashStart, hashEnd - hashStart).Select(i => LineContent(raw, lines[i])))
                    : string.Empty;
                string currentLinesHash = ComputeHash(Encoding.UTF8.GetBytes(covered));

                string want = NormalizeHash(expectedHash);
                if (!string.Equals(want, currentFileHash, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(want, currentLinesHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前文件 hash: {currentFileHash}；"
                        + $"line {offset}..{offset + Math.Max(limit, 1) - 1} hash: {currentLinesHash}；期望: {want}）。"
                        + "请用 read_file 重新确认内容后重试，或不传 expected_hash。",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            if (offset > totalLines + 1)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"OUT_OF_RANGE：offset={offset} 超出文件末行（共 {totalLines} 行，最大可插入位置 {totalLines + 1}）。"
                    + "行号口径与 read_file 一致（1-based）。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            int startIdx = offset - 1;
            int endIdx = Math.Min(startIdx + limit, totalLines);
            int replacedLines = Math.Max(0, endIdx - startIdx);

            string head = startIdx < totalLines ? raw.Substring(0, lines[startIdx].Start) : raw;
            string tail = endIdx < totalLines ? raw.Substring(lines[endIdx].Start) : string.Empty;
            bool needLeadingEol = startIdx >= totalLines && head.Length > 0 && !EndsWithEol(head);

            string? blockEol = null;
            if (replacedLines > 0 && lines[endIdx - 1].EolLength > 0)
            {
                blockEol = raw.Substring(lines[endIdx - 1].Start + lines[endIdx - 1].ContentLength, lines[endIdx - 1].EolLength);
            }
            else if (startIdx < totalLines && lines[startIdx].EolLength > 0)
            {
                blockEol = raw.Substring(lines[startIdx].Start + lines[startIdx].ContentLength, lines[startIdx].EolLength);
            }

            string eol = blockEol ?? detected.NewLine;

            bool needTrailingEol;
            if (tail.Length > 0)
            {
                needTrailingEol = true;
            }
            else if (replacedLines > 0)
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

            var rebuilt = new StringBuilder(raw.Length + newTextLf.Length + 16);
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
            string updatedText = rebuilt.ToString();

            if (string.Equals(updatedText, raw, StringComparison.Ordinal))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"NO_CHANGE：line {offset}..{offset + Math.Max(replacedLines, 1) - 1} 的新文本与现有内容完全一致，未做写入。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            try
            {
                FileToolShared.AtomicWriteAllText(fullPath, updatedText, detected.Encoding, detected.Preamble);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail($"文件写回失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds));
            }

            string newHash = HashText(updatedText, detected);
            var (diff, diffTruncated) = EditDiffBuilder.Build(
                relative,
                FileToolShared.NormalizeToLf(raw),
                FileToolShared.NormalizeToLf(updatedText),
                EditDiffBuilder.DefaultContextLines,
                MaxSuccessOutputChars - 380);

            var sb = new StringBuilder();
            sb.Append("已按行号修改 '").Append(relative).Append("'（line ")
              .Append(offset).Append("..").Append(offset + Math.Max(replacedLines, 1) - 1)
              .Append(" → ").Append(newLineCount).Append(" 行；Encoding: ").Append(detected.EncodingLabel)
              .Append(", LineEndings: ").Append(detected.NewLineLabel)
              .Append(", hash: ").Append(newHash).AppendLine("）");

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
                EditFileTool.ClampOutput(sb.ToString(), MaxSuccessOutputChars),
                durationMs: sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"edit_lines 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }

    internal readonly struct RawLine
    {
        public RawLine(int start, int contentLength, int eolLength)
        {
            Start = start;
            ContentLength = contentLength;
            EolLength = eolLength;
        }

        /// <summary>该行内容在原文本中的起始偏移。</summary>
        public int Start { get; }

        /// <summary>行内容长度（不含换行符）。</summary>
        public int ContentLength { get; }

        /// <summary>行尾换行符长度（0 = 文件末行无换行）。</summary>
        public int EolLength { get; }
    }

    /// <summary>
    /// 按物理行切分原文本（与 read_file 的 content.Split('\n') 行数完全一致）：
    /// 支持 CRLF / LF / 混合换行，记录每行内容起点、内容长度与换行符长度。
    /// </summary>
    internal static List<RawLine> SplitRawLines(string raw)
    {
        var list = new List<RawLine>();
        int i = 0;
        int start = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\r' || c == '\n')
            {
                int eolLength = c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
                list.Add(new RawLine(start, i - start, eolLength));
                i += eolLength;
                start = i;
                continue;
            }

            i++;
        }

        list.Add(new RawLine(start, raw.Length - start, 0));
        return list;
    }

    private static string LineContent(string raw, RawLine line) =>
        line.ContentLength <= 0 ? string.Empty : raw.Substring(line.Start, line.ContentLength);

    private static bool EndsWithEol(string text) =>
        text.Length > 0 && (text[text.Length - 1] == '\n' || text[text.Length - 1] == '\r');

    internal static string ComputeHash(byte[] payload)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(payload);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>整文件字节 sha256（expected_hash 的主口径，与磁盘字节完全一致）。</summary>
    private static string HashFileBytes(string fullPath, FileToolShared.DetectedText detected)
    {
        try
        {
            return ComputeHash(File.ReadAllBytes(fullPath));
        }
        catch
        {
            // 极端情况（文件被外部锁）退化为按解码文本计算
            return ComputeHash(Encoding.UTF8.GetBytes(detected.Content));
        }
    }

    /// <summary>按原编码（含 BOM）计算写出后文本的字节 hash，与 HashFileBytes 口径一致。</summary>
    private static string HashText(string text, FileToolShared.DetectedText detected)
    {
        byte[] body = detected.Encoding.GetBytes(text);
        if (detected.Preamble.Length == 0)
        {
            return ComputeHash(body);
        }

        byte[] payload = new byte[detected.Preamble.Length + body.Length];
        Buffer.BlockCopy(detected.Preamble, 0, payload, 0, detected.Preamble.Length);
        Buffer.BlockCopy(body, 0, payload, detected.Preamble.Length, body.Length);
        return ComputeHash(payload);
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
}
