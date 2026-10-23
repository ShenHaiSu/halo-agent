using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// grep_search：纯 C# 跨代正则内容检索（Regex + 并行扫描，无外部二进制依赖）。
/// 最多返回 100 条匹配。
/// </summary>
[HaloAgent.Tools.Tool("grep_search")]
public sealed class GrepSearchTool : IToolHandler
{
    public const int MaxMatches = 100;
    public const int MaxFileSizeBytes = 5 * 1024 * 1024;
    public const int MaxLineDisplayChars = 300;

    private static readonly HashSet<string> ExcludedDirNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "node_modules",
            "bin",
            "obj",
            "dist",
            "__pycache__",
            ".vs",
        };

    public string Name => "grep_search";

    // NEED17 PromptBrief: Search file contents with regex
    public string Description =>
        "Search file contents in the workspace with a regular expression. "
        + "Returns up to 100 matches as relative_path:line_number: matched_line. "
        + "Directories .git/node_modules/bin/obj/dist are skipped. Long lines cut at 300 chars; use read_file for the full line. "
        + "Large outputs are truncated to head+tail within a fixed budget; the full text is spilled to a log file path in the result. Use read_file(offset,limit) or grep_search to page, never re-run a broad command to dump everything.";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString(
                "pattern",
                "Regular expression to search. Returns relative_path:line_number:matched_line, up to 100 matches. Empty result is normal (no match), not an error.",
                required: true
            )
            .AddString(
                "path",
                "Optional subdirectory or file to search in. Defaults to workspace root. Directories .git/node_modules/bin/obj/dist are skipped."
            )
            .AddString(
                "include_glob",
                "Optional file filter like \"*.cs\" (multiple patterns separated by ';', e.g. *.cs;*.ts)."
            )
            .Build();
        return new(Name, Description, schema);
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? pattern = null;
            string? pathArg = null;
            string? includeGlob = null;
            try
            {
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    pattern = args.GetString("pattern");
                    pathArg = args.GetString("path");

                    // NEED17 compat: alias 'file_path'→'path' (02 shim搬运; handler双读不断老调用).
                    if (string.IsNullOrWhiteSpace(pathArg))
                    {
                        pathArg = args.GetString("file_path");
                    }

                    includeGlob = args.GetString("include_glob");
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 pattern/path/include_glob 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                return ToolResult.Fail(
                    "缺少必需参数 'pattern'（正则表达式）。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                return ToolResult.Fail(
                    $"正则表达式非法：{ex.Message}。请修正 pattern 后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            // need22-P0 S1：检索起点强制 ValidateRead（越界一律 PATH_OUTSIDE_WORKSPACE，不得降级）
            string startPath;
            {
                var (ok, err, resolved, _) = PathSafetyValidator.ValidateRead(
                    workspaceRoot,
                    string.IsNullOrWhiteSpace(pathArg) ? "." : pathArg
                );
                if (!ok)
                {
                    return ToolResult.Fail(err!, durationMs: sw.ElapsedMilliseconds);
                }
                startPath = resolved;
            }

            var files = new List<string>();
            if (File.Exists(startPath))
            {
                files.Add(startPath);
            }
            else if (Directory.Exists(startPath))
            {
                CollectFiles(startPath, files);
            }
            else
            {
                return ToolResult.Fail(
                    $"搜索路径不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, startPath)}'。请用 list_dir 确认路径后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            List<Regex> globFilters = BuildGlobFilters(includeGlob);
            var matches = new List<(string Rel, int Line, string Text)>();
            var matchesLock = new object();
            int scannedFiles = 0;
            int totalHits = 0;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.CancellationToken
            );

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = linkedCts.Token,
            };

            try
            {
                await Parallel
                    .ForEachAsync(
                        files,
                        options,
                        async (file, token) =>
                        {
                            if (Volatile.Read(ref totalHits) >= MaxMatches)
                            {
                                return;
                            }

                            if (!IsGlobMatch(Path.GetFileName(file), globFilters))
                            {
                                return;
                            }

                            try
                            {
                                var info = new FileInfo(file);
                                if (
                                    info.Length > MaxFileSizeBytes
                                    || FileToolShared.LooksBinary(file)
                                )
                                {
                                    return;
                                }
                            }
                            catch (Exception)
                            {
                                return;
                            }

                            List<(int Line, string Text)>? fileHits = await ScanFileAsync(
                                    file,
                                    regex,
                                    token
                                )
                                .ConfigureAwait(false);
                            if (fileHits == null || fileHits.Count == 0)
                            {
                                Interlocked.Increment(ref scannedFiles);
                                return;
                            }

                            Interlocked.Increment(ref scannedFiles);
                            string rel = PathSafetyValidator.GetWorkspaceRelativePath(
                                workspaceRoot,
                                file
                            );
                            lock (matchesLock)
                            {
                                foreach (var hit in fileHits)
                                {
                                    if (totalHits >= MaxMatches)
                                    {
                                        break;
                                    }

                                    matches.Add((rel, hit.Line, hit.Text));
                                    totalHits++;
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested
                    || context.CancellationToken.IsCancellationRequested
                )
            {
                return ToolResult.Fail("搜索已被取消。", durationMs: sw.ElapsedMilliseconds);
            }

            matches.Sort(
                (a, b) =>
                {
                    int c = string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : a.Line.CompareTo(b.Line);
                }
            );

            var sb = new StringBuilder();
            if (matches.Count == 0)
            {
                sb.Append(
                    $"未找到匹配 '{pattern}' 的内容（已扫描 {scannedFiles} 个文件）。可放宽 include_glob 或检查正则后重试。"
                );
            }
            else
            {
                foreach (var m in matches)
                {
                    sb.Append(m.Rel)
                        .Append(':')
                        .Append(m.Line)
                        .Append(": ")
                        .AppendLine(TruncateLine(m.Text));
                }

                sb.Append($"共 {matches.Count} 处匹配");
                if (totalHits >= MaxMatches)
                {
                    sb.Append($"（已达上限 {MaxMatches} 条，请缩小范围或收紧 pattern）");
                }

                sb.Append($"，扫描 {scannedFiles} 个文件。");
            }

            return ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail(
                $"grep_search 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds
            );
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("搜索已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        finally
        {
            sw.Stop();
        }
    }

    private static void CollectFiles(string dir, List<string> files)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception)
        {
            return;
        }

        foreach (string entry in entries)
        {
            try
            {
                // need22-P0 S1：跳过 symlink/junction（防区内软链穿越到区外被检索）
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    // 属性读取失败交由后续判定
                }
                if (Directory.Exists(entry))
                {
                    if (ExcludedDirNames.Contains(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    CollectFiles(entry, files);
                }
                else
                {
                    files.Add(entry);
                }
            }
            catch (Exception)
            {
                // 忽略单个条目异常
            }
        }
    }

    private static List<Regex> BuildGlobFilters(string? includeGlob)
    {
        var result = new List<Regex>();
        if (string.IsNullOrWhiteSpace(includeGlob))
        {
            return result;
        }

        string[] parts = includeGlob.Split(
            new[] { ';', ',', '|' },
            StringSplitOptions.RemoveEmptyEntries
        );
        foreach (string raw in parts)
        {
            string glob = raw.Trim();
            if (string.IsNullOrEmpty(glob))
            {
                continue;
            }

            // 简易 glob → 正则：* → .*, ? → .
            string regexText =
                "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try
            {
                result.Add(
                    new Regex(regexText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                );
            }
            catch (ArgumentException)
            {
                // 忽略非法 glob 片段
            }
        }

        return result;
    }

    private static bool IsGlobMatch(string fileName, List<Regex> filters)
    {
        if (filters.Count == 0)
        {
            return true;
        }

        foreach (Regex filter in filters)
        {
            if (filter.IsMatch(fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<List<(int Line, string Text)>?> ScanFileAsync(
        string file,
        Regex regex,
        CancellationToken ct
    )
    {
        try
        {
            byte[] raw = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            string content = SmartConsoleDecoder.TryDecodeStrictUtf8(raw, out string strict)
                ? strict
                : SmartConsoleDecoder.GbkEncoding.GetString(raw);

            var hits = new List<(int, string)>();
            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].EndsWith("\r")
                    ? lines[i].Substring(0, lines[i].Length - 1)
                    : lines[i];
                bool isMatch;
                try
                {
                    isMatch = regex.IsMatch(line);
                }
                catch (Exception)
                {
                    return hits;
                }

                if (isMatch)
                {
                    hits.Add((i + 1, line));
                    if (hits.Count >= MaxMatches)
                    {
                        break;
                    }
                }
            }

            return hits;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TruncateLine(string line)
    {
        if (line.Length <= MaxLineDisplayChars)
        {
            return line;
        }

        return line.Substring(0, MaxLineDisplayChars) + $"...（行超长已截断，原 {line.Length} 字符）";
    }
}
