using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// edit：精确替换。
///
/// ============ 三重校验 ============
///   1. old_string 为空 → 报错（空串会匹配任何位置，是最危险的形式）
///   2. 匹配 0 次 → 报错（带总行数/换行/hash/续读指针，不瞎猜）
///   3. 匹配多次 → 报错（带命中行号，要求扩大上下文使其唯一）
///
/// 不做“替换第一个”/“替换全部”这种“聪明”选择：
/// 在改代码时，猜错比失败更糟。
/// </summary>
public sealed class EditTool : ITool
{
    public string Name => "edit";

    public string Description =>
        "精确替换文件中某段文本(先定位 old_string 再替换为 new_string)。写前自动备份。" +
        "若 old_string 出现多次,请带上足够上下文使其唯一";

    public string? PromptSnippet => "精确替换文件中的文本";

    public IReadOnlyList<string> PromptGuidelines => new[]
    {
        "改文件用 edit；old_string 必须与文件内容逐字一致且唯一，不唯一就多带几行上下文",
        "同一文件的多处改动分成多次 edit，不要一次塞进一个超长 old_string",
        "改文件标准流：grep_search 定位 → read_file 小窗口(offset/limit)确认 → 最小 old_string 单次 edit → 用返回的改后周边行验证",
        "禁止用被截断的 read 结果拼 write_file 全量覆写",
    };

     public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
       ""type"": ""object"",
       ""properties"": {
         ""path"":       { ""type"": ""string"", ""description"": ""要修改的文件路径"" },
         ""old_string"": { ""type"": ""string"", ""description"": ""要替换的原文，必须与文件逐字节一致(含缩进/换行)；多处命中需加上下文使其唯一；行号前缀不是内容"" },
         ""new_string"": { ""type"": ""string"", ""description"": ""替换后文本；删除该段请传空串"" }
       },
       ""required"": [""path"", ""old_string"", ""new_string""]
     }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.WriteFile;

    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rawPath = ReadRequired(ctx, args, "path");
        var oldStr = ReadRequired(ctx, args, "old_string");
        var newStr = ReadRequired(ctx, args, "new_string");

        if (rawPath is null) return ToolResult.Failure("path 不能为空");
        if (oldStr is null) return ToolResult.Failure("old_string 不能为空");
        if (newStr is null) return ToolResult.Failure("new_string 不能为空");
        if (oldStr.Length == 0) return ToolResult.Failure("old_string 不能为空");

         var resolved = ToolPaths.ResolveChecked(ctx, rawPath);
         if (resolved.Error is not null) return ToolResult.Failure(resolved.Error);
         var full = resolved.Path!;
         // 目录 vs 不存在是两种完全不同的下一步：目录 → 换 list_directory；
         // 「文件不存在 vs 父目录不存在」留给下面的读取分支区分（FileNotFound / DirectoryNotFound）
         if (Directory.Exists(full))
             return ToolResult.Failure($"这是一个目录，不是文件: {full}。查看目录请用 list_directory");

         return await FileMutationQueue.RunAsync(full, async () =>
         {
             byte[] raw;
             string content;
             try
             {
                 raw = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                 content = System.Text.Encoding.UTF8.GetString(raw);
                 // 去 BOM（显示与匹配口径统一：BOM 不是内容）
                 if (content.Length > 0 && content[0] == '\uFEFF')
                     content = content[1..];
             }
             catch (FileNotFoundException) { return ToolResult.Failure($"文件不存在: {full}"); }
             catch (DirectoryNotFoundException)
             {
                 var parent = Path.GetDirectoryName(full);
                 return ToolResult.Failure($"文件所在目录不存在: {parent}（请检查路径，或先用 write_file 创建）");
             }
             catch (Exception ex) { return ToolResult.Failure($"读取 {full} 失败: {ex.Message}。请检查路径是否正确"); }

            var sw = Stopwatch.StartNew();
            bool oversized = raw.Length > 1024 * 1024;
            int totalLines = EditFeedback.CountLines(content);
            string newlineKind = EditFeedback.DetectNewlineKind(content);
            bool hasBom = EditFeedback.HasBom(raw);
            string shortHash = EditFeedback.ComputeShortHash(content);

            int occ = EditFeedback.FindOccurrences(content, oldStr).Count;
            if (occ == 0)
            {
                int pointerLimit = Math.Min(Math.Max(totalLines, 0), 50);
                if (pointerLimit <= 0) pointerLimit = 50;
                var msg = EditFeedback.BuildZeroMatchMessage(
                    full, totalLines, newlineKind, shortHash, hasBom, 0, pointerLimit);
                return ToolResult.Failure(msg);
            }
            if (occ > 1)
            {
                // 性能：超大文件或扫描超 50ms 降级为只给续读指针
                if (oversized || sw.ElapsedMilliseconds > 50)
                {
                    var minimal = $"old_string 在文件中出现 {occ} 次，未写入。请加上下文使其唯一。\n" +
                        $"重读其一确认：read_file(path=\"{full}\", offset=0, limit=50)";
                    return ToolResult.Failure(minimal);
                }
                var indices = EditFeedback.FindOccurrences(content, oldStr);
                var starts = EditFeedback.GetLineStartOffsets(content);
                var hits = new List<(int StartLine, int EndLine, string Summary)>();
                foreach (var idx in indices)
                {
                    int sLine = EditFeedback.IndexToLine(starts, idx);
                    int eIdx = Math.Min(idx + oldStr.Length - 1, Math.Max(content.Length - 1, 0));
                    int eLine = content.Length == 0 ? 1 : EditFeedback.IndexToLine(starts, Math.Max(eIdx, 0));
                    var firstLineText = EditFeedback.GetLineText(content, sLine);
                    hits.Add((sLine, eLine, EditFeedback.SummarizeLine(firstLineText)));
                    if (sw.ElapsedMilliseconds > 50) break;
                }
                if (sw.ElapsedMilliseconds > 50)
                {
                    var minimal = $"old_string 在文件中出现 {occ} 次，未写入。请加上下文使其唯一。\n" +
                        $"重读其一确认：read_file(path=\"{full}\", offset=0, limit=50)";
                    return ToolResult.Failure(minimal);
                }
                int firstLine = hits.Count > 0 ? hits[0].StartLine : 1;
                int pOff = Math.Max(0, firstLine - 3);
                var msg = EditFeedback.BuildMultiMatchMessage(full, hits, pOff, 20);
                return ToolResult.Failure(msg);
            }

            if (string.Equals(oldStr, newStr, StringComparison.Ordinal))
                return ToolResult.Failure(
                    $"未做修改: new_string 与 old_string 完全相同，文件 {full} 内容不变。" +
                    "如果本意就是删除该段文本，请把 new_string 传空字符串");

            var backup = await ctx.Host.BackupAsync(full, "edit", ct).ConfigureAwait(false);
            if (!backup.BackedUp && backup.Error is not null)
                return ToolResult.Failure($"备份失败，已取消修改: {backup.Error}");

            var updated = content.Replace(oldStr, newStr, StringComparison.Ordinal);
            try { await File.WriteAllTextAsync(full, updated, System.Text.Encoding.UTF8, ct).ConfigureAwait(false); }
            catch (Exception ex) { return ToolResult.Failure($"写入 {full} 失败: {ex.Message}"); }

            // 成功回包：行号 + hash + 周边（封顶 1000 字符）
            int startIdx = content.IndexOf(oldStr, StringComparison.Ordinal);
            var newStarts = EditFeedback.GetLineStartOffsets(updated);
            int sLineNew = newStarts.Length == 0 ? 1 : EditFeedback.IndexToLine(newStarts, Math.Min(startIdx, Math.Max(updated.Length - 1, 0)));
            int eIdxNew = startIdx + Math.Max(newStr.Length - 1, 0);
            int eLineNew = newStarts.Length == 0 ? 1 : EditFeedback.IndexToLine(newStarts, Math.Min(eIdxNew, Math.Max(updated.Length - 1, 0)));
            string newHash = EditFeedback.ComputeShortHash(updated);
            string surround = EditFeedback.ExtractSurrounding(updated, sLineNew, eLineNew);
            return ToolResult.Success(EditFeedback.BuildSuccessMessage(full, sLineNew, eLineNew, newHash, surround));
        }).ConfigureAwait(false);
    }

    private static string? ReadRequired(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null)
        {
            try { return ctx.TypedArgs.RequiredString(name); }
            catch (InvalidOperationException) { return null; }
        }
        Debug.WriteLine($"[edit] TypedArgs 缺失，走 legacy 分支 name={name}");
        return args.GetStringOrNull(name);
    }
}
