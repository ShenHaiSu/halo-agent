using System.Diagnostics;
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
        "内容来自被截断的 read 时禁止直接覆写；同一文件多改分多次 edit",
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""path"":    { ""type"": ""string"", ""description"": ""目标文件路径,相对或绝对"" },
        ""content"": { ""type"": ""string"", ""description"": ""完整文件内容；只改局部请用 edit；内容来自被截断的 read 时禁止直接覆写"" },
        ""expectedHash"": { ""type"": ""string"", ""description"": ""可选乐观并发：read/edit 成功回包里的短 hash；传入则写前比对，不一致拒绝"" }
      },
      ""required"": [""path"", ""content""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.WriteFile;

    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var rawPath = ReadRequired(ctx, args, "path");
        var content = ReadRequired(ctx, args, "content");
        var expectedHash = ReadOptional(ctx, args, "expectedHash");
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
            // 乐观并发：在队列 lambda 内比对（CAS），否则 TOCTOU
            if (!string.IsNullOrEmpty(expectedHash) && File.Exists(full))
            {
                byte[] curRaw;
                try { curRaw = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false); }
                catch (Exception ex) { return ToolResult.Failure($"读取 {full} 失败: {ex.Message}"); }
                var curText = System.Text.Encoding.UTF8.GetString(curRaw);
                if (curText.Length > 0 && curText[0] == '\uFEFF') curText = curText[1..];
                var actual = EditFeedback.ComputeShortHash(curText);
                if (!string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Failure($"文件已被改动（期望 {expectedHash.Trim()} 实际 {actual}），请重读后再写");
            }

            // ② undo 快照（失败则不写）
            var backup = await ctx.Host.BackupAsync(full, "write_file", ct).ConfigureAwait(false);
            if (!backup.BackedUp && backup.Error is not null)
                return ToolResult.Failure($"备份失败，已取消写入: {backup.Error}");

            // ③ 写入
            if (content.Contains('\uFFFD'))
                Debug.WriteLine($"[write_file] content contains U+FFFD path={full}");

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
             int lines = EditFeedback.CountLines(content);
             string newHash = EditFeedback.ComputeShortHash(content);
             var note = createdParent ? $"（已自动创建目录 {parent}）" : "";
             return ToolResult.Success(EditFeedback.BuildWriteSuccessMessage(full, bytes, lines, newHash, note));
        }).ConfigureAwait(false);
    }

    private static string? ReadRequired(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null)
        {
            try { return ctx.TypedArgs.RequiredString(name); }
            catch (InvalidOperationException) { return null; }
        }
        Debug.WriteLine($"[write_file] TypedArgs 缺失，走 legacy 分支 name={name}");
        return args.GetStringOrNull(name);
    }

    private static string? ReadOptional(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null)
        {
            if (!ctx.TypedArgs.Has(name)) return null;
            try { return ctx.TypedArgs.RequiredString(name); }
            catch (InvalidOperationException) { return ctx.TypedArgs.StringOrNull(name); }
        }
        return args.GetStringOrNull(name);
    }
}
