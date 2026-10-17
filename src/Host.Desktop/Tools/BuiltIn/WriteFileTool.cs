using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// write_file：新建或全量覆写文件。自动建父目录、继承原编码换行、原子落盘。
/// </summary>
public sealed class WriteFileTool : IToolHandler
{
    public string Name => "write_file";

    public string Description => "Create a new text file or completely overwrite an existing file.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Relative path from workspace root or absolute file path.\"},"
        + "\"content\":{\"type\":\"string\",\"description\":\"Full text content to write to the file.\"}"
        + "},\"required\":[\"file_path\",\"content\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string? content = null;
            bool hasContent = false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("file_path", out JsonElement fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    filePathArg = fpEl.GetString();
                }

                if (root.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    content = contentEl.GetString();
                    hasContent = true;
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/content 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(ToolResult.Fail(
                    "缺少必需参数 'file_path'。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (!hasContent || content == null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "缺少必需参数 'content'（如需清空文件请传空字符串 \"\"）。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            var (allowed, error, fullPath, outsideWorkspace) =
                PathSafetyValidator.ValidateWrite(workspaceRoot, filePathArg);
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(error!, durationMs: sw.ElapsedMilliseconds));
            }

            bool isNew = !File.Exists(fullPath);
            Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            string encodingLabel = "UTF-8";
            string newLineLabel = "LF";
            byte[] preamble = Array.Empty<byte>();
            string payload = content;

            if (!isNew)
            {
                // 覆盖已存在文件：继承原编码与换行符风格，避免 Git 全行标红
                try
                {
                    FileToolShared.DetectedText detected = FileToolShared.ReadAllTextWithDetection(fullPath);
                    encoding = detected.Encoding;
                    encodingLabel = detected.EncodingLabel;
                    preamble = detected.Preamble;
                    payload = FileToolShared.ConvertNewLines(content, detected.NewLine);
                    newLineLabel = detected.NewLineLabel;
                }
                catch (Exception ex)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"读取原文件编码失败，写入已中止以防损坏：{ex.Message}",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            try
            {
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                FileToolShared.AtomicWriteAllText(fullPath, payload, encoding, preamble);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件写入失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            int lineCount = string.IsNullOrEmpty(payload) ? 0 : payload.Replace("\r\n", "\n").Split('\n').Length;
            long byteSize = new FileInfo(fullPath).Length;
            string display = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);
            string action = isNew ? "已创建新文件" : "已覆盖文件";
            var sb = new StringBuilder();
            sb.Append(action).Append(" '").Append(display).Append("'：")
              .Append(lineCount).Append(" 行 / ").Append(byteSize).Append(" 字节")
              .Append(" (Encoding: ").Append(encodingLabel)
              .Append(", LineEndings: ").Append(newLineLabel).Append(')');
            if (outsideWorkspace)
            {
                sb.Append(" [警告：目标位于当前工作区之外，请确认路径无误]");
            }

             var ok = ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds);
             ok.FileBytes = byteSize;
             return Task.FromResult(ok);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"write_file 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }
}
