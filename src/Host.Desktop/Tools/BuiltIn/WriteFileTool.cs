using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// write_file：新建或全量覆写文件 + need15 安全护栏。
/// P0-2：覆盖需 allow_overwrite + size 上限 + expected_hash + 备份；
/// P2-1 dry_run；P1-3 hash 回吐。
/// </summary>
public sealed class WriteFileTool : IToolHandler
{
    /// <summary>need15 P0-2：默认上限 250KB（约 256_000 字节），可热调。</summary>
    internal static int DefaultMaxWriteBytes = 256_000;

    /// <summary>need15 P0-2：硬上限 1MB，max_bytes 不得超过。</summary>
    internal const int HardMaxWriteBytes = 1_000_000;

    public string Name => "write_file";

    public string Description =>
        "Create a new file, or fully overwrite an existing file ONLY when allow_overwrite:true is passed. " +
        "For partial edits prefer edit_file/edit_lines. Overwrites are backed up. " +
        "Example: {\"file_path\":\"src/new.ts\",\"content\":\"export const x = 1;\\n\"} (new file)";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Relative path from workspace root or absolute file path.\"},"
        + "\"content\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Full text content to write to the file. For existing files >250KB use edit_* instead; truncated content will be rejected. For existing files you must also pass allow_overwrite:true.\"},"
        + "\"allow_overwrite\":{\"type\":\"boolean\",\"description\":\"Required true to overwrite an existing file. Default false.\"},"
        + "\"expected_hash\":{\"type\":\"string\",\"description\":\"Optional sha256 of the existing file bytes; mismatch rejects the overwrite.\"},"
        + "\"max_bytes\":{\"type\":\"integer\",\"description\":\"Optional override of the size cap (bytes). Must not exceed 1000000.\"},"
        + "\"dry_run\":{\"type\":\"boolean\",\"description\":\"If true, only validate and preview without writing.\"}"
        + "},\"required\":[\"file_path\",\"content\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string? content = null;
            bool hasContent = false;
            bool allowOverwrite = false;
            string? expectedHash = null;
            int? maxBytesReq = null;
            bool dryRun = false;
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

                if (root.TryGetProperty("allow_overwrite", out JsonElement owEl) && IsBool(owEl))
                {
                    allowOverwrite = owEl.GetBoolean();
                }

                if (root.TryGetProperty("expected_hash", out JsonElement hashEl) && hashEl.ValueKind == JsonValueKind.String)
                {
                    expectedHash = hashEl.GetString();
                }

                if (root.TryGetProperty("max_bytes", out JsonElement maxEl) && maxEl.ValueKind == JsonValueKind.Number
                    && maxEl.TryGetInt32(out int parsedMax))
                {
                    maxBytesReq = parsedMax;
                }

                if (root.TryGetProperty("dry_run", out JsonElement dryEl) && IsBool(dryEl))
                {
                    dryRun = dryEl.GetBoolean();
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
            int effectiveMax = DefaultMaxWriteBytes;
            if (maxBytesReq.HasValue)
            {
                if (maxBytesReq.Value > HardMaxWriteBytes)
                {
                    return Task.FromResult(ToolResult.FailCode("CONTENT_TOO_LARGE",
                        $"CONTENT_TOO_LARGE：max_bytes={maxBytesReq.Value} 超过硬上限 {HardMaxWriteBytes}。请改用 edit_file/edit_lines 分片改。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                if (maxBytesReq.Value > 0)
                {
                    effectiveMax = maxBytesReq.Value;
                }
            }

            Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            string encodingLabel = "UTF-8";
            string newLineLabel = "LF";
            byte[] preamble = Array.Empty<byte>();
            string payload = content;
            FileToolShared.DetectedText? detected = null;

            if (!isNew)
            {
                // 覆盖已存在文件：继承原编码与换行符风格
                try
                {
                    detected = FileToolShared.ReadAllTextWithDetection(fullPath);
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

                // P0-2 按序校验：size → allow_overwrite → expected_hash
                long contentBytes = encoding.GetByteCount(payload) + preamble.Length;
                if (contentBytes > effectiveMax)
                {
                    return Task.FromResult(ToolResult.FailCode("CONTENT_TOO_LARGE",
                        $"CONTENT_TOO_LARGE：全量内容 {contentBytes} 字节超过上限 {effectiveMax} 字节（默认 256000）。请改用 edit_file/edit_lines 分片改。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                if (!allowOverwrite)
                {
                    long curBytes;
                    string curHash;
                    int curLines;
                    try
                    {
                        byte[] curRaw = File.ReadAllBytes(fullPath);
                        curBytes = curRaw.Length;
                        curHash = FileToolShared.ComputeHash(curRaw);
                        curLines = detected.Content.Length == 0 ? 0 : detected.Content.Replace("\r\n", "\n").Split('\n').Length;
                    }
                    catch
                    {
                        curBytes = -1;
                        curHash = "unknown";
                        curLines = -1;
                    }

                    return Task.FromResult(ToolResult.FailCode("NEED_CONFIRM_OVERWRITE",
                        $"NEED_CONFIRM_OVERWRITE：目标已存在（{curLines} 行/{curBytes} 字节/hash:{curHash}）。确认全量覆盖请重调并传 allow_overwrite:true；局部改请用 edit_file/edit_lines。",
                        durationMs: sw.ElapsedMilliseconds));
                }

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
                            $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前文件 hash: {currentHash}；期望: {want}）。请用 read_file 重新确认内容后重试，或不传 expected_hash。",
                            durationMs: sw.ElapsedMilliseconds));
                    }
                }
            }
            else
            {
                // 新建文件：仍受硬上限保护（防 OOM），默认上限不拦截新建以保持兼容
                long contentBytes = Encoding.UTF8.GetByteCount(content);
                if (contentBytes > HardMaxWriteBytes)
                {
                    return Task.FromResult(ToolResult.FailCode("CONTENT_TOO_LARGE",
                        $"CONTENT_TOO_LARGE：全量内容 {contentBytes} 字节超过硬上限 {HardMaxWriteBytes}。请分片写入。",
                        durationMs: sw.ElapsedMilliseconds));
                }
            }

            // P2-1 dry_run：校验通过，只预览不写盘
            if (dryRun)
            {
                string previewHash = detected != null
                    ? FileToolShared.ComputeTextHash(payload, detected)
                    : FileToolShared.ComputeHash(encoding.GetBytes(payload));
                int lineCount = string.IsNullOrEmpty(payload) ? 0 : payload.Replace("\r\n", "\n").Split('\n').Length;
                long byteSize = encoding.GetByteCount(payload) + preamble.Length;
                var preview = new StringBuilder();
                preview.Append("DRY_RUN：").Append(isNew ? "新建" : "覆盖").Append("预览（未写盘），")
                    .Append(lineCount).Append(" 行/").Append(byteSize).Append(" 字节，hash_preview: ").AppendLine(previewHash);
                if (!isNew)
                {
                    preview.AppendLine("（dry_run 不产生备份；实写时将自动备份原文件。）");
                }

                return Task.FromResult(ToolResult.Ok(preview.ToString(), durationMs: sw.ElapsedMilliseconds));
            }

            // P0-2/P2-2：落盘前备份（覆盖场景；备份失败阻塞）
            string? backupShown = null;
            if (!isNew)
            {
                try
                {
                    var (bakPath, _, _) = BackupStore.WriteBackup(fullPath, context.DataRoot, context.WorkspaceId, context.SessionId, "write_file");
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

            int finalLines = string.IsNullOrEmpty(payload) ? 0 : payload.Replace("\r\n", "\n").Split('\n').Length;
            long byteSizeFinal = new FileInfo(fullPath).Length;
            string display = PathSafetyValidator.GetWorkspaceRelativePath(workspaceRoot, fullPath);
            string action = isNew ? "已创建新文件" : "已覆盖文件";
            string finalHash;
            try
            {
                finalHash = FileToolShared.ComputeHash(File.ReadAllBytes(fullPath));
            }
            catch
            {
                finalHash = detected != null ? FileToolShared.ComputeTextHash(payload, detected) : "unknown";
            }

            var sb = new StringBuilder();
            sb.Append(action).Append(" '").Append(display).Append("')：")
              .Append(finalLines).Append(" 行 / ").Append(byteSizeFinal).Append(" 字节")
              .Append(" (Encoding: ").Append(encodingLabel)
              .Append(", LineEndings: ").Append(newLineLabel)
              .Append(", hash: ").Append(finalHash);
            if (backupShown != null)
            {
                sb.Append(", backup: ").Append(backupShown);
            }

            sb.Append(')');
            if (outsideWorkspace)
            {
                sb.Append(" [警告：目标位于当前工作区之外，请确认路径无误]");
            }

             var ok = ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds);
             ok.FileBytes = byteSizeFinal;
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

    private static bool IsBool(JsonElement el) =>
        el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False;

    private static string NormalizeHash(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("sha256:".Length).Trim();
        }

        return trimmed;
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
}
