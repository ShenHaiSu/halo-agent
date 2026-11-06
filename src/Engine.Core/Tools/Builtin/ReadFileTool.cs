using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

 /// <summary>
 /// read_file：逐行流式读取。
 ///
 /// ============ 为什么不能 File.ReadAllText ============
 /// 一个几 GB 的日志会把进程内存直接打爆。
 /// 逐行读只把要展示的行留在内存里，同时数出总行数用于“共 N 行”。
 ///
 /// ============ 边界（都有专门测试） ============
 ///   - offset 越界（超出文件末尾）：返回 Failure（带合法范围），不渲染空区间表头
 ///   - offset < 0：返回 Failure（行号从 0 计，不支持倒数）
 ///   - limit <= 0：返回 Failure（limit 必须为正整数）
 ///   - limit 极大：用饱和加法，不让 start + n 溢出成 end &lt; start
 ///   - 中文/emoji：按行读（行本身是完整 UTF-8）
 /// </summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description =>
        "读取文本文件内容(支持分页)。适合查看文件、代码、配置、日志等。" +
        $"单次最多返回 {ToolLimits.ReadDefaultMaxLines} 行或 {ToolLimits.ReadMaxChars} 字符(先到者为准)，" +
        $"超出会给出续读参数；单行超过 {ToolLimits.ReadMaxLineChars} 字符会截断";

    public string? PromptSnippet => "读取文件内容";

    public IReadOnlyList<string> PromptGuidelines => new[]
    {
        "查看文件用 read_file，不要用 run_shell_command 跑 cat/type/sed",
        "文件很长时用 offset/limit 分段读，按返回的续读提示继续",
    };

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
         ""path"":   { ""type"": ""string"", ""description"": ""文件路径,相对或绝对"" },
         ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""从第几行开始读,从 0 计,显示行号从 1 起,缺省从开头"" },
         ""limit"":  { ""type"": ""integer"", ""minimum"": 1, ""description"": ""读多少行,缺省读全部(受默认行数上限约束)"" },
         ""visible_whitespace"": { ""type"": ""boolean"", ""description"": ""是否可视化空白(空格显·/tab显⇥/行尾显$/换行类型显式)；仅 edit 失败后对齐用，正常阅读保持关闭"" }
      },
      ""required"": [""path""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
         var pathRaw = ReadRequired(ctx, args, "path");
         if (string.IsNullOrWhiteSpace(pathRaw)) return ToolResult.Failure("path 不能为空");
         if (pathRaw.Contains('\0')) return ToolResult.Failure("path 含非法字符");
         var path = ToolPaths.Resolve(ctx.Cwd, pathRaw);
         var missing = ToolPaths.DescribeMissing(path);
         if (missing is not null) return ToolResult.Failure(missing);

         var start = ReadLong(ctx, args, "offset") ?? 0;
         if (start < 0)
             return ToolResult.Failure(
                 $"offset 不能为负(收到 {start})。行号从 0 开始；" +
                 "若想从文件末尾往前读，请改用 offset = max(0, 总行数 - N)，或先不带 offset 读一次拿到总行数");
         var limitRaw = ReadLong(ctx, args, "limit");
         if (limitRaw is not null && limitRaw <= 0)
             return ToolResult.Failure($"limit 必须为正整数(收到 {limitRaw})");
         var limit = limitRaw ?? ToolLimits.ReadDefaultMaxLines;
         bool visibleWhitespace = ReadBool(ctx, args, "visible_whitespace") ?? false;

        // 饱和加法：防溢出（模型可以传 ulong.MaxValue 这种）
        long stop = limitRaw is null && limit == ToolLimits.ReadDefaultMaxLines && (ReadLong(ctx, args, "limit") is null)
            ? start + ToolLimits.ReadDefaultMaxLines
            : start > long.MaxValue - limit ? long.MaxValue : start + limit;
        // limit 未传时按默认上限
        if (limitRaw is null) stop = start > long.MaxValue - ToolLimits.ReadDefaultMaxLines ? long.MaxValue : start + ToolLimits.ReadDefaultMaxLines;

        var wanted = new List<string>();
        long total = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, bufferSize: 64 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;

                if (total >= start && total < stop) wanted.Add(line);
                total++;

                // 已经有足够的行且超出 stop：仍要数完总行数（但要快）
                // 优化：超过 stop 后继续数但不再分配字符串
                if (total >= stop)
                {
                    // 快速计数剩余行
                    var rest = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (rest.Length > 0)
                    {
                        total += 1 + rest.Count(c => c == '\n')
                                 - (rest.EndsWith('\n') ? 1 : 0);
                    }
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Failure($"没有读取权限: {path}");
        }
        catch (IOException ex)
        {
            return ToolResult.Failure($"读取 {path} 失败: {ex.Message}");
        }

         long shownStart = Math.Min(start, total);

         // offset 越界：明确 Failure（带合法范围），不渲染「77-76」这类空区间表头
         if (wanted.Count == 0 && total > 0 && start >= total)
             return ToolResult.Failure(
                 $"offset={start} 超出文件末尾(共 {total} 行)。请用 offset 在 0-{total - 1} 之间，或省略 offset 从头读");

         string newlineKind = DetectNewlineKindFast(path);
         var sb = new StringBuilder();
         if (wanted.Count == 0)
             sb.Append($"文件 {path} 共 {total} 行（换行 {newlineKind}）。\n");
         else
             sb.Append($"文件 {path} 共 {total} 行（换行 {newlineKind}），显示第 {shownStart + 1}-{shownStart + wanted.Count} 行；行号从 1 起，调用 offset 从 0 起，offset={shownStart + wanted.Count} 可续读。\n");
         sb.Append("行号前缀“   6 | ”不是文件内容，勿抄入 old_string。\n");
        int usedChars = 0;
        bool charCapped = false;
        int outLines = 0;
        for (int i = 0; i < wanted.Count; i++)
        {
            var line = wanted[i];
            bool lineTruncated = false;
            if (line.Length > ToolLimits.ReadMaxLineChars)
            {
                line = line[..ToolLimits.ReadMaxLineChars];
                lineTruncated = true;
            }
            if (visibleWhitespace)
                line = RenderVisibleWhitespace(line);

            var rendered = $"{shownStart + i + 1,6} | {line}\n";
            if (usedChars + rendered.Length > ToolLimits.ReadMaxChars)
            {
                charCapped = true;
                break;
            }
            usedChars += rendered.Length;
            sb.Append(rendered);
            outLines++;
            if (lineTruncated) sb.Append($"       |(本行超过 {ToolLimits.ReadMaxLineChars} 字符，已截断)\n");
        }

        // 3) 续读提示（可直接照抄下一次调用）
        int nextOffset = (int)(shownStart + outLines);
        if (charCapped)
            sb.Append($"\n[已达 {ToolLimits.ReadMaxChars} 字符上限。用 offset={nextOffset} 继续读取]\n");
        else if (shownStart + outLines < total)
            sb.Append($"\n[共 {total} 行，已显示到第 {shownStart + outLines} 行。用 offset={nextOffset} 继续读取]\n");
        return ToolResult.Success(sb.ToString());
    }

    internal static string RenderVisibleWhitespace(string line) =>
        line.Replace(" ", "·").Replace("\t", "⇥") + "$";

    internal static string DetectNewlineKindFast(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, false);
            var buf = new byte[65536];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i + 1 < n; i++)
                if (buf[i] == (byte)'\r' && buf[i + 1] == (byte)'\n')
                    return "CRLF";
            return "LF";
        }
        catch { return "LF"; }
    }

    private static string? ReadRequired(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null)
        {
            try { return ctx.TypedArgs.RequiredString(name); }
            catch (InvalidOperationException) { return null; }
        }
        Debug.WriteLine($"[read_file] TypedArgs 缺失，走 legacy 分支 name={name}");
        return args.GetStringOrNull(name);
    }

    private static long? ReadLong(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null) return ctx.TypedArgs.LongOrNull(name);
        return args.GetLongOrNull(name);
    }

    private static bool? ReadBool(ToolContext ctx, JsonElement args, string name)
    {
        if (ctx.TypedArgs is not null) return ctx.TypedArgs.BoolOrNull(name);
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
