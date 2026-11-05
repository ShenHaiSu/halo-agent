using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// edit：精确替换。
///
/// ============ 三重校验 ============
///   1. old_string 为空 → 报错（空串会匹配任何位置，是最危险的形式）
///   2. 匹配 0 次 → 报错（提示“注意精确匹配，含缩进/空白”）
///   3. 匹配多次 → 报错（要求扩大上下文使其唯一）
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
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""path"":       { ""type"": ""string"", ""description"": ""要修改的文件路径"" },
        ""old_string"": { ""type"": ""string"", ""description"": ""要替换的原文(必须精确匹配)"" },
        ""new_string"": { ""type"": ""string"", ""description"": ""替换成的新文本"" }
      },
      ""required"": [""path"", ""old_string"", ""new_string""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.WriteFile;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rawPath = ctx.TypedArgs?.StringOrNull("path") ?? args.GetStringOrNull("path");
        var oldStr = ctx.TypedArgs?.StringOrNull("old_string") ?? args.GetStringOrNull("old_string");
        var newStr = ctx.TypedArgs?.StringOrNull("new_string") ?? args.GetStringOrNull("new_string");

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
             string content;
             try { content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false); }
             catch (FileNotFoundException) { return ToolResult.Failure($"文件不存在: {full}"); }
             catch (DirectoryNotFoundException)
             {
                 var parent = Path.GetDirectoryName(full);
                 return ToolResult.Failure($"文件所在目录不存在: {parent}（请检查路径，或先用 write_file 创建）");
             }
             catch (Exception ex) { return ToolResult.Failure($"读取 {full} 失败: {ex.Message}。请检查路径是否正确"); }

            int occ = CountOccurrences(content, oldStr);
            if (occ == 0)
                return ToolResult.Failure(
                    "未找到要替换的文本(old_string 不匹配)。请检查文件实际内容后重试，" +
                    "注意精确匹配(含缩进/空白)");
            if (occ > 1)
                return ToolResult.Failure(
                    $"old_string 在文件中出现 {occ} 次，请扩大上下文使其唯一");

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

            return ToolResult.Success($"已替换 {full} 中的 1 处文本。");
        }).ConfigureAwait(false);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
            if (idx >= haystack.Length) break;
        }
        return count;
    }
}
