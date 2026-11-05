using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// write_file：创建或整体覆写。
///
/// ============ 写前必做 undo 快照 ============
/// 这是“可回滚”的唯一保证。备份失败时<b>不写</b>（宁可失败也不能丢原文件）。
/// </summary>
public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";

    public string Description =>
        "创建新文件或整体覆写已有文件。覆写已有文件前系统会自动备份到 undo 目录";

    public string? PromptSnippet => "新建或覆写文件";

    public IReadOnlyList<string> PromptGuidelines => new[]
    {
        "新建文件或整体覆写用 write_file；只改局部用 edit",
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""path"":    { ""type"": ""string"", ""description"": ""目标文件路径,相对或绝对"" },
        ""content"": { ""type"": ""string"", ""description"": ""完整的新文件内容"" }
      },
      ""required"": [""path"", ""content""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.WriteFile;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rawPath = ctx.TypedArgs?.StringOrNull("path") ?? args.GetStringOrNull("path");
        var content = ctx.TypedArgs?.StringOrNull("content") ?? args.GetStringOrNull("content");
        if (rawPath is null) return ToolResult.Failure("path 不能为空");
        if (content is null) return ToolResult.Failure("content 不能为空");

        var path = ToolPaths.ResolveChecked(ctx, rawPath);
        if (path.Error is not null) return ToolResult.Failure(path.Error);

        var full = path.Path!;

         // ① 父目录自动创建（静默建目录会掩盖拼写错误，成功文案必须注明做了什么）
         var parent = Path.GetDirectoryName(full);
         bool createdParent = false;
         if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
         {
             try { Directory.CreateDirectory(parent); createdParent = true; }
             catch (Exception ex) { return ToolResult.Failure($"创建目录 {parent} 失败: {ex.Message}"); }
         }

        return await FileMutationQueue.RunAsync(full, async () =>
        {
            // ② undo 快照（失败则不写）
            var backup = await ctx.Host.BackupAsync(full, "write_file", ct).ConfigureAwait(false);
            if (!backup.BackedUp && backup.Error is not null)
                return ToolResult.Failure($"备份失败，已取消写入: {backup.Error}");

            // ③ 写入
            // ★ D9-A3：写盘前检查 U+FFFD。只 Debug 留痕、不拒绝写入（用户可能真的想写这个符号）。
            //   正常 SSE 解码修好后这里不应再出现；若出现，说明上游仍有解码点漏网。
            if (content.Contains('\uFFFD'))
                System.Diagnostics.Debug.WriteLine($"[write_file] content contains U+FFFD path={full}");

            try
            {
                await File.WriteAllTextAsync(full, content, System.Text.Encoding.UTF8, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolResult.Failure($"写入 {full} 失败: {ex.Message}");
            }

             var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
             var note = createdParent ? $"（已自动创建目录 {parent}）" : "";
             return ToolResult.Success($"已写入 {full} ({bytes} 字节){note}");
        }).ConfigureAwait(false);
    }
}
