using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public string Description =>
        "列出目录下的条目(文件与子目录名),用于了解目录结构。缺省列出当前工作目录";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""path"": { ""type"": ""string"", ""description"": ""要列出的目录路径,相对或绝对;缺省当前工作目录"" }
      },
      ""required"": []
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var raw = args.GetStringOrNull("path");
        var dir = raw is null ? ctx.Cwd : ToolPaths.Resolve(ctx.Cwd, raw);

        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Failure($"目录不存在: {dir}"));

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir)
                .Select(p =>
                {
                    var name = Path.GetFileName(p);
                    return Directory.Exists(p) ? name + "/" : name;
                })
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failure($"无法读取目录 {dir}: {ex.Message}"));
        }

        var body = entries.Count == 0 ? "(空目录)" : string.Join("\n", entries);
        return Task.FromResult(ToolResult.Success($"目录 {dir}:\n{body}"));
    }
}
