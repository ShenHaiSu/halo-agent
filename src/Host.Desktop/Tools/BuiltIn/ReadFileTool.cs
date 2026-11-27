using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// read_file：带 1-based 行号分页与 BOM/UTF-8/GBK 自动识别的文件读取工具。
/// </summary>
[HaloAgent.Tools.Tool("read_file")]
public sealed class ReadFileTool : IToolHandler
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;

    public string Name => "read_file";

    // NEED17 PromptBrief: Read file content with 1-based paging
    // NEED17 PromptRules: Use read_file to view files, not cat/type via exec_command.
    public string Description =>
        "Read a text file from the workspace with 1-based line numbers. "
        + "Supports pagination via offset and limit for large files. "
        + "Returns 1-based line numbers. Large outputs are truncated (head+tail) with the full text spilled to a log file you can page with offset/limit.";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString(
                "file_path",
                "Relative path from workspace root or absolute file path. Use list_dir first if unsure. "
                    + "Directories are rejected with IS_A_DIRECTORY; use list_dir to browse.",
                required: true
            )
            .AddInteger(
                "offset",
                "1-based first line. Out-of-range returns 0 lines with an explicit OFFSET_OUT_OF_RANGE marker; check TotalLines in the header.",
                minimum: 1,
                defaultValue: 1
            )
            .AddInteger(
                "limit",
                "Max lines to return. Capped at 2000; more lines need next offset. Text beyond the tool output budget is truncated and spilled to a file (see guide).",
                minimum: 1,
                maximum: 2000,
                defaultValue: 500
            )
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
            int offset = 1;
            int limit = DefaultLimit;
            try
            {
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    filePathArg = args.GetString("file_path");
                    if (args.TryGetInt32("offset", out int parsedOffset))
                    {
                        offset = parsedOffset;
                    }
                    if (args.TryGetInt32("limit", out int parsedLimit))
                    {
                        limit = parsedLimit;
                    }
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/offset/limit 字段后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        "缺少必需参数 'file_path'。请先用 list_dir 确认文件路径后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            // need22-P0 S1：读路径强制 ValidateRead（越界一律 PATH_OUTSIDE_WORKSPACE，不得降级）
            string fullPath;
            {
                // 会话输出目录只读豁免：本会话落盘全文可分页读回。
                var (ok, err, resolved, _) = PathSafetyValidator.ValidateRead(
                    workspaceRoot,
                    filePathArg,
                    SessionOutputRoots.GetSessionReadAllowedRoots(context)
                );
                if (!ok)
                {
                    return Task.FromResult(
                        ToolResult.Fail(err!, durationMs: sw.ElapsedMilliseconds)
                    );
                }
                // 落盘唯一形态为 call_*.txt：命中本会话输出目录但非该形态的单文件仍按越界拒；
                // 目录本身放行（后续走 IS_A_DIRECTORY 分支）。
                if (!SessionOutputRoots.IsSessionOutputFileName(resolved))
                {
                    string spillDir = SessionOutputRoots.BuildSessionToolOutputsDir(
                        context.DataRoot,
                        context.WorkspaceId,
                        context.SessionId
                    );
                    try
                    {
                        string normalizedResolved = Path.GetFullPath(resolved)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string normalizedSpillDir = Path.GetFullPath(spillDir)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (
                            !string.Equals(
                                normalizedResolved,
                                normalizedSpillDir,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && normalizedResolved.StartsWith(
                                normalizedSpillDir + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            return Task.FromResult(
                                ToolResult.Fail(
                                    $"{PathSafetyValidator.Codes.OutsideWorkspace}：拒绝工作区之外的读取：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, resolved)}'。请使用工作区内相对路径后重试。",
                                    durationMs: sw.ElapsedMilliseconds
                                )
                            );
                        }
                    }
                    catch
                    {
                        // 路径规范化失败则沿用已放行的 resolved，不额外拦截。
                    }
                }
                fullPath = resolved;
            }

            // 目录的 File.Exists 为 false，必须先判类型再判存在性
            if (Directory.Exists(fullPath))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "IS_A_DIRECTORY",
                        $"IS_A_DIRECTORY：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}' 是目录而非文件。"
                            + "请用 list_dir 列出目录内容，或用 read_file 指定目录下的具体文件。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"文件不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。请使用 list_dir 确认路径后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (FileToolShared.LooksBinary(fullPath))
            {
                long size = new FileInfo(fullPath).Length;
                return Task.FromResult(
                    ToolResult.Fail(
                        $"拒绝按文本读取二进制文件：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'（{size} 字节，检测到 NUL 字节）。图片/可执行文件/压缩包请改用 exec_command 处理。",
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
            string fileHash;
            try
            {
                fileHash = FileToolShared.ComputeHash(File.ReadAllBytes(fullPath));
            }
            catch
            {
                fileHash = FileToolShared.ComputeTextHash(detected.Content, detected);
            }

            string hashShown = fileHash;

            bool offsetOutOfRange = offset > totalLines;
            sb.Append("[Encoding: ")
                .Append(detected.EncodingLabel)
                .Append(", LineEndings: ")
                .Append(detected.NewLineLabel)
                .Append(", TotalLines: ")
                .Append(totalLines)
                .Append(", Hash: ")
                .Append(hashShown);
            if (offsetOutOfRange)
            {
                sb.Append(", OffsetRequested: ").Append(offset).Append(", EmptyPage: true");
            }
            sb.Append(']');
            if (offsetOutOfRange)
            {
                sb.AppendLine()
                    .Append(
                        $"⚠ OFFSET_OUT_OF_RANGE: offset {offset} 超出文件总行数 {totalLines}（合法范围 1..{totalLines}），本页 0 行。"
                    );
                sb.AppendLine().Append($"offset {offset} 超出文件总行数 {totalLines}，无内容返回。");
                sb.AppendLine().Append($"returned_lines:0/total_lines:{totalLines}");
                return Task.FromResult(
                    ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds)
                );
            }

            int endExclusive = Math.Min(totalLines, offset - 1 + limit);
            int width = endExclusive.ToString().Length;
            for (int i = offset; i <= endExclusive; i++)
            {
                sb.AppendLine()
                    .Append(i.ToString().PadLeft(width))
                    .Append(": ")
                    .Append(lines[i - 1]);
            }

            if (endExclusive < totalLines)
            {
                sb.AppendLine()
                    .Append(
                        $"... 还有 {totalLines - endExclusive} 行未显示，可用 offset={endExclusive + 1} limit={limit} 继续分页读取。"
                    );
            }

            return Task.FromResult(
                ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds)
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"read_file 执行异常：{ex.Message}。请修正参数后重试。",
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
