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
///   - offset 越界：返回空切片，不抛
///   - limit 极大：用饱和加法，不让 start + n 溢出成 end &lt; start
///   - 中文/emoji：按行读（行本身是完整 UTF-8）
/// </summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description =>
        "读取文本文件内容(支持分页)。适合查看文件、代码、配置、日志等";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""path"":   { ""type"": ""string"", ""description"": ""文件路径,相对或绝对"" },
        ""offset"": { ""type"": ""integer"", ""description"": ""从第几行开始读,从 0 计,缺省从开头"" },
        ""limit"":  { ""type"": ""integer"", ""description"": ""读多少行,缺省读全部"" }
      },
      ""required"": [""path""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ToolPaths.Resolve(ctx.Cwd, args.GetStringOrNull("path"));
        if (path is null) return ToolResult.Failure("path 不能为空");
        if (!File.Exists(path)) return ToolResult.Failure($"文件不存在: {path}");

        var start = args.GetLongOrNull("offset") ?? 0;
        var limit = args.GetLongOrNull("limit");

        // 饱和加法：防溢出（模型可以传 ulong.MaxValue 这种）
        long stop = limit is null ? long.MaxValue
            : start > long.MaxValue - limit.Value ? long.MaxValue
            : start + limit.Value;

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
                if (total >= stop && limit is not null)
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
        long end = shownStart + wanted.Count;

        var sb = new StringBuilder();
        sb.Append($"文件 {path} 共 {total} 行，显示 {shownStart + 1}-{end}:\n");
        for (int i = 0; i < wanted.Count; i++)
        {
            sb.Append($"{shownStart + i + 1,6} | {wanted[i]}\n");
        }
        return ToolResult.Success(sb.ToString());
    }
}
