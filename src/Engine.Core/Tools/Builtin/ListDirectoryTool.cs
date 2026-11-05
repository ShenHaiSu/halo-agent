using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public string Description =>
        "列出目录下的条目(文件与子目录名),用于了解目录结构。缺省列出当前工作目录" +
        $"。缺省返回前 {ToolLimits.LsDefaultLimit} 项，最多 {ToolLimits.LsMaxLimit} 项";

    public string? PromptSnippet => "了解目录结构";

    public IReadOnlyList<string> PromptGuidelines => new[]
    {
        "不确定文件在哪时，先用 list_directory 看清目录再下手",
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(
        "{\"type\": \"object\"," +
        "\"properties\": {" +
        "\"path\":  { \"type\": \"string\", \"description\": \"要列出的目录路径,相对或绝对;缺省当前工作目录\" }," +
        "\"limit\": { \"type\": \"integer\", \"description\": \"最多返回条数，缺省 " + ToolLimits.LsDefaultLimit + "，最多 " + ToolLimits.LsMaxLimit + "\" }" +
        "}," +
        "\"required\": []}").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var raw = ctx.TypedArgs?.StringOrNull("path") ?? args.GetStringOrNull("path");
        var dir = raw is null ? ctx.Cwd : ToolPaths.Resolve(ctx.Cwd, raw);

        if (!Directory.Exists(dir))
            return Task.FromResult(ToolResult.Failure($"目录不存在: {dir}"));

        var limitRaw = ctx.TypedArgs?.LongOrNull("limit") ?? args.GetLongOrNull("limit");
        var limit = (int)Math.Clamp(limitRaw ?? ToolLimits.LsDefaultLimit, 1, ToolLimits.LsMaxLimit);

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

        var shown = entries.Take(limit).ToList();
        var body = shown.Count == 0 ? "(空目录)" : string.Join("\n", shown);
        var sb = new StringBuilder();
        sb.Append($"目录 {dir}(共 {entries.Count} 项):\n{body}");
        if (entries.Count > shown.Count)
            sb.Append($"\n…(还有 {entries.Count - shown.Count} 项未显示；调大 limit(最多 {ToolLimits.LsMaxLimit}) 可看更多)");
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
