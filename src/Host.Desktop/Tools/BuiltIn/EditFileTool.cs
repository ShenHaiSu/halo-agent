using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// edit_file：CRLF/LF 容错精确替换，唯一性校验，生成 Unified Diff（±3 行）。
/// </summary>
public sealed class EditFileTool : IToolHandler
{
    public string Name => "edit_file";

    public string Description =>
        "Perform an exact string replacement in an existing text file. " +
        "Prefer this over write_file when modifying existing files.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Target file path.\"},"
        + "\"old_string\":{\"type\":\"string\",\"description\":\"Exact existing text block to replace (include 2-5 lines of surrounding context to ensure uniqueness).\"},"
        + "\"new_string\":{\"type\":\"string\",\"description\":\"Replacement text block.\"},"
        + "\"replace_all\":{\"type\":\"boolean\",\"description\":\"If true, replace all occurrences. If false (default), old_string must match exactly once.\"}"
        + "},\"required\":[\"file_path\",\"old_string\",\"new_string\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string? oldString = null;
            string? newString = null;
            bool replaceAll = false;
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

                if (root.TryGetProperty("replace_all", out JsonElement allEl) &&
                    (allEl.ValueKind == JsonValueKind.True || allEl.ValueKind == JsonValueKind.False))
                {
                    replaceAll = allEl.GetBoolean();
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

            // 换行归一化后匹配（LLM 生成 \n，Windows 文件多为 \r\n）
            string contentLf = FileToolShared.NormalizeToLf(detected.Content);
            string oldLf = FileToolShared.NormalizeToLf(oldString);
            string newLf = FileToolShared.NormalizeToLf(newString);

            int matchCount = FileToolShared.CountOccurrences(contentLf, oldLf);
            if (matchCount == 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    "Error: old_string not found in file. Please use read_file to check the exact current content.",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (matchCount > 1 && !replaceAll)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"Error: old_string matched {matchCount} times. Please include more surrounding context lines or set replace_all: true.",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string updatedLf = replaceAll
                ? contentLf.Replace(oldLf, newLf, StringComparison.Ordinal)
                : ReplaceFirst(contentLf, oldLf, newLf);

            // 还原原换行符风格并按原编码原子写回
            string updated = FileToolShared.ConvertNewLines(updatedLf, detected.NewLine);
            try
            {
                FileToolShared.AtomicWriteAllText(fullPath, updated, detected.Encoding, detected.Preamble);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件写回失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string relative = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);
            string diff = BuildDiff(relative, contentLf, updatedLf);
            int replaced = replaceAll ? matchCount : 1;

            var sb = new StringBuilder();
            sb.Append("替换成功（").Append(replaced).Append(" 处）：'").Append(relative).Append('\'')
              .Append(" (Encoding: ").Append(detected.EncodingLabel)
              .Append(", LineEndings: ").Append(detected.NewLineLabel).AppendLine(")")
              .AppendLine("```diff").Append(diff);
            if (!diff.EndsWith("\n"))
            {
                sb.AppendLine();
            }

            sb.AppendLine("```");

            return Task.FromResult(ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds));
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

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            return haystack;
        }

        return haystack.Substring(0, index) + replacement + haystack.Substring(index + needle.Length);
    }

    /// <summary>
    /// 生成 Unified Diff：单处精确 hunk（±3 行），多处合并为整体 hunk。
    /// </summary>
    private static string BuildDiff(string relativePath, string oldContentLf, string newContentLf)
    {
        string[] oldLines = oldContentLf.Split('\n');
        string[] newLines = newContentLf.Split('\n');

        // 行级前后缀公共部分
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

        int ctxStart = Math.Max(1, oldChangeStart - 3);
        int ctxEndOld = Math.Min(oldLines.Length, oldChangeStart + oldChangeCount - 1 + 3);
        int newCtxStart = Math.Max(1, newChangeStart - 3);
        int newCtxEnd = Math.Min(newLines.Length, newChangeStart + newChangeCount - 1 + 3);

        var sb = new StringBuilder();
        sb.Append("--- a/").AppendLine(relativePath);
        sb.Append("+++ b/").AppendLine(relativePath);
        sb.Append("@@ -").Append(ctxStart).Append(',').Append(Math.Max(0, ctxEndOld - ctxStart + 1));
        sb.Append(" +").Append(newCtxStart).Append(',').Append(Math.Max(0, newCtxEnd - newCtxStart + 1));
        sb.AppendLine(" @@");

        for (int i = ctxStart; i < oldChangeStart; i++)
        {
            sb.Append(' ').AppendLine(oldLines[i - 1]);
        }

        for (int i = 0; i < oldChangeCount; i++)
        {
            sb.Append('-').AppendLine(oldLines[oldChangeStart - 1 + i]);
        }

        for (int i = 0; i < newChangeCount; i++)
        {
            sb.Append('+').AppendLine(newLines[newChangeStart - 1 + i]);
        }

        for (int i = oldChangeStart + oldChangeCount; i <= ctxEndOld; i++)
        {
            sb.Append(' ').AppendLine(oldLines[i - 1]);
        }

        return sb.ToString();
    }
}
