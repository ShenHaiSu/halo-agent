using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// read_file：带 1-based 行号分页与 BOM/UTF-8/GBK 自动识别的文件读取工具。
/// </summary>
public sealed class ReadFileTool : IToolHandler
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;

    public string Name => "read_file";

    public string Description =>
        "Read a text file from the workspace with 1-based line numbers. " +
        "Supports pagination via offset and limit for large files.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Relative path from workspace root or absolute file path.\"},"
        + "\"offset\":{\"type\":\"integer\",\"description\":\"1-based starting line number. Defaults to 1.\"},"
        + "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum number of lines to read. Defaults to 500, max 2000.\"}"
        + "},\"required\":[\"file_path\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            int offset = 1;
            int limit = DefaultLimit;
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
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/offset/limit 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(ToolResult.Fail(
                    "缺少必需参数 'file_path'。请先用 list_dir 确认文件路径后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            string fullPath = PathSafetyValidator.ResolveReadPath(workspaceRoot, filePathArg);

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件不存在：'{fullPath}'。请使用 list_dir 确认路径后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (FileToolShared.LooksBinary(fullPath))
            {
                long size = new FileInfo(fullPath).Length;
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝按文本读取二进制文件：'{fullPath}'（{size} 字节，检测到 NUL 字节）。图片/可执行文件/压缩包请改用 exec_command 处理。",
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

            if (offset < 1)
            {
                offset = 1;
            }

            if (limit < 1)
            {
                limit = DefaultLimit;
            }

            limit = Math.Min(limit, MaxLimit);

            string[] lines = detected.Content.Split('\n');
            int totalLines = lines.Length;
            // 统一去掉每行末尾残留 \r（CRLF 文件），显示时不再携带
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].EndsWith("\r"))
                {
                    lines[i] = lines[i].Substring(0, lines[i].Length - 1);
                }
            }

            var sb = new StringBuilder();
            sb.Append("[Encoding: ").Append(detected.EncodingLabel)
              .Append(", LineEndings: ").Append(detected.NewLineLabel)
              .Append(", TotalLines: ").Append(totalLines).Append(']');

            if (offset > totalLines)
            {
                sb.AppendLine().Append($"offset {offset} 超出文件总行数 {totalLines}，无内容返回。");
                return Task.FromResult(ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds));
            }

            int endExclusive = Math.Min(totalLines, offset - 1 + limit);
            int width = endExclusive.ToString().Length;
            for (int i = offset; i <= endExclusive; i++)
            {
                sb.AppendLine().Append(i.ToString().PadLeft(width)).Append(": ").Append(lines[i - 1]);
            }

            if (endExclusive < totalLines)
            {
                sb.AppendLine().Append($"... 还有 {totalLines - endExclusive} 行未显示，可用 offset={endExclusive + 1} limit={limit} 继续分页读取。");
            }

            return Task.FromResult(ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"read_file 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }
}
