using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

public sealed class GrepSearchTool : ITool
{
    private static readonly HashSet<string> NoiseDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "target", "bin", "obj", ".vs", ".idea",
        "dist", "build", ".next", "__pycache__", ".venv", "venv",
    };

    public string Name => "grep_search";
    public string Description =>
        "在文件中按正则搜索文本内容,返回 路径:行号:内容" +
        $"。缺省返回前 {ToolLimits.GrepDefaultLimit} 条，最多 {ToolLimits.GrepMaxLimit} 条" +
        $"；可选 context=N 附带前后 N 行（最多 {ToolLimits.GrepMaxContextLines} 行）";

    public string? PromptSnippet => "按内容搜索文件";

    public IReadOnlyList<string> PromptGuidelines => new[]
    {
        "找内容用 grep_search，不要跑 findstr/grep/Select-String",
        "正则写简单些，复杂正则容易超时",
        "改文件先 grep 定位，再小窗口读确认，避免整文件灌入上下文",
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(
        "{\"type\": \"object\"," +
        "\"properties\": {" +
        "\"pattern\": { \"type\": \"string\", \"description\": \"正则表达式,如 'TODO|FIXME'\" }," +
        "\"path\":    { \"type\": \"string\", \"description\": \"搜索目录或文件,缺省当前工作目录\" }," +
        "\"glob\":    { \"type\": \"string\", \"description\": \"只搜索匹配该 glob 的文件,如 '*.rs'\" }," +
        "\"limit\":   { \"type\": \"integer\", \"description\": \"最多返回条数，缺省 " + ToolLimits.GrepDefaultLimit + "，最多 " + ToolLimits.GrepMaxLimit + "\" }," +
        "\"context\": { \"type\": \"integer\", \"minimum\": 0, \"maximum\": " + ToolLimits.GrepMaxContextLines + ", \"description\": \"命中前后附带 N 行上下文，缺省 0（仅命中行）\" }" +
        "}," +
        "\"required\": [\"pattern\"]}").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pattern = ReadRequired(ctx, args, "pattern");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(ToolResult.Failure("pattern 不能为空"));

         Regex re;
         try
         {
             // 超时保护：防灾难性回溯（用户输入的正则不可信）
             re = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(ToolLimits.GrepRegexTimeoutSeconds));
         }
         catch (ArgumentException ex)
         {
             // offset 是定位问题的关键，必须保留；常见笔误（字符范围写反）给一句中文提示
             var hint = ex.Message.Contains("range in reverse order", StringComparison.OrdinalIgnoreCase)
                 ? "（提示：字符范围顺序颠倒，如 [z-a] 应改为 [a-z]）"
                 : "";
             return Task.FromResult(ToolResult.Failure($"正则表达式无效: {ex.Message}{hint}"));
         }

        var pathRaw = ReadOptional(ctx, args, "path");
        var target = pathRaw is null ? ctx.Cwd : ToolPaths.Resolve(ctx.Cwd, pathRaw);
        var globFilter = ReadOptional(ctx, args, "glob");

        var limitRaw = ReadLong(ctx, args, "limit");
        var limit = (int)Math.Clamp(limitRaw ?? ToolLimits.GrepDefaultLimit, 1, ToolLimits.GrepMaxLimit);
        var contextRaw = ReadLong(ctx, args, "context");
        var context = (int)Math.Clamp(contextRaw ?? 0, 0, ToolLimits.GrepMaxContextLines);

        var files = new List<string>();
        if (File.Exists(target)) files.Add(target);
        else if (Directory.Exists(target)) CollectFiles(target, files, ct);
        else return Task.FromResult(ToolResult.Failure($"路径不存在: {target}"));

        var results = new List<string>();
        int scanned = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (globFilter is not null)
            {
                var name = Path.GetFileName(file);
                var rel = Path.GetRelativePath(ctx.Cwd, file).Replace('\\', '/');
                if (!GlobTool.SimpleGlobMatch(globFilter, name) &&
                    !GlobTool.SimpleGlobMatch(globFilter, rel) &&
                    !file.EndsWith(globFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (fi.Length > ToolLimits.GrepMaxFileBytes) continue;

            string text;
            try { text = File.ReadAllText(file); } catch { continue; }   // 二进制/不可读跳过
            if (text.Contains('\0')) continue;   // 二进制

            scanned++;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                bool hit;
                try { hit = re.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { hit = false; }

                if (!hit) continue;

                var shown = lines[i].TrimEnd('\r');
                if (shown.Length > ToolLimits.GrepMaxLineChars) shown = shown.Substring(0, ToolLimits.GrepMaxLineChars) + "…";
                if (context == 0)
                {
                    results.Add($"{file}:{i + 1}:{shown}");
                }
                else
                {
                    var sbHit = new StringBuilder();
                    sbHit.Append($"{file}:{i + 1}:{shown}");
                    int from = Math.Max(0, i - context);
                    int to = Math.Min(lines.Length - 1, i + context);
                    for (int k = from; k <= to; k++)
                    {
                        if (k == i) continue;
                        var ctxLine = lines[k].TrimEnd('\r');
                        if (ctxLine.Length > ToolLimits.GrepMaxLineChars) ctxLine = ctxLine[..ToolLimits.GrepMaxLineChars] + "…";
                        sbHit.Append($"\n  {file}:{k + 1}:{ctxLine}");
                        if (sbHit.Length > 2000) { sbHit.Append("\n  …(上下文已截断)"); break; }
                    }
                    results.Add(sbHit.ToString());
                }

                if (results.Count >= limit * 2) break;   // 多收一点用于计数
            }
            if (results.Count >= limit * 2) break;
        }

        if (results.Count == 0)
            return Task.FromResult(ToolResult.Success($"没有匹配结果（扫描 {scanned} 个文件）"));

        var sb = new StringBuilder();
        sb.Append($"找到 {results.Count} 条匹配（扫描 {scanned} 个文件）:\n");
        foreach (var r in results.Take(limit)) sb.Append(r).Append('\n');
        if (results.Count > limit)
            sb.Append($"…(还有 {results.Count - limit} 条未显示；调大 limit 或缩小 path)\n");

        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }

    private static string? ReadRequired(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null)
        {
            try { return ctx.TypedArgs.RequiredString(name); }
            catch (InvalidOperationException) { return null; }
        }
        Debug.WriteLine($"[grep_search] TypedArgs 缺失，走 legacy 分支 name={name}");
        return args.GetStringOrNull(name);
    }

    private static string? ReadOptional(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null) return ctx.TypedArgs.StringOrNull(name);
        return args.GetStringOrNull(name);
    }

    private static long? ReadLong(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null) return ctx.TypedArgs.LongOrNull(name);
        return args.GetLongOrNull(name);
    }

    private static void CollectFiles(string root, List<string> files, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }

            foreach (var e in entries)
            {
                if (Directory.Exists(e))
                {
                    var name = Path.GetFileName(e);
                    if (!NoiseDirs.Contains(name)) stack.Push(e);
                }
                else
                {
                    files.Add(e);
                }
            }
        }
    }
}
