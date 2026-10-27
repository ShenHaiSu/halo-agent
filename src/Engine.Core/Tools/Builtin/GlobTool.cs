using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// glob：按模式查找文件。
///
/// ============ 关键：迭代式单星号回溯 ============
/// 朴素递归实现在 `*a*a*a*a*…b` 这类 pattern 上是指数级（2^n），
/// 而 pattern 来自模型输出，可以被恶意构造。这里用“最近星号 + 回溯点”的
/// 经典算法，最坏 O(n·m)，绝不爆炸。
///
/// ============ 语义 ============
///   - `*` 与 `**` 等价：都跨 `/`
///   - `?` 匹配单个字符
///   - 不含 `/` 的模式自动加 `**/` 前缀（“找任意目录下的 X”）
///   - 跳过噪音目录（.git / node_modules / target / bin / obj）
/// </summary>
public sealed class GlobTool : ITool
{
    private static readonly HashSet<string> NoiseDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "target", "bin", "obj", ".vs", ".idea",
        "dist", "build", ".next", "__pycache__", ".venv", "venv",
    };

    public string Name => "glob";
    public string Description => "按文件名/路径模式查找文件,模式支持通配符 * ? 与 ** 递归";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""pattern"": { ""type"": ""string"", ""description"": ""glob 模式,如 '**/*.zip' 或 'src/**/*.rs'"" },
        ""path"":    { ""type"": ""string"", ""description"": ""搜索起点目录,缺省为当前工作目录"" }
      },
      ""required"": [""pattern""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var pattern = args.GetStringOrNull("pattern");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(ToolResult.Failure("pattern 不能为空"));

        var rootRaw = args.GetStringOrNull("path");
        var root = rootRaw is null ? ctx.Cwd : ToolPaths.Resolve(ctx.Cwd, rootRaw);
        if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Failure($"目录不存在: {root}"));

        if (!pattern.Contains('/')) pattern = "**/" + pattern;

        var matches = new List<string>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos(); }
            catch { continue; }   // 权限不足等：跳过

            foreach (var child in children)
            {
                var rel = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');

                if (SimpleGlobMatch(pattern, rel))
                    matches.Add(child.FullName);

                if (child is DirectoryInfo sub && !NoiseDirs.Contains(sub.Name))
                    stack.Push(sub);
            }
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);

        if (matches.Count == 0)
            return Task.FromResult(ToolResult.Success($"在 {root} 下没有匹配 {pattern} 的文件"));

        var shown = matches.Take(500).ToList();
        var sb = new StringBuilder();
        sb.Append($"匹配 {matches.Count} 个文件:\n");
        foreach (var m in shown) sb.Append(m).Append('\n');
        if (matches.Count > shown.Count)
            sb.Append($"…(还有 {matches.Count - shown.Count} 个未显示)\n");

        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }

    /// <summary>
    /// 极简 glob 匹配（迭代式，无递归）。
    ///
    /// 算法：
    ///   pi/ti 双指针；遇到 `*` 记录回溯点（star=pi 之后的非星位置，star_ti=ti）；
    ///   匹配失败且存在回溯点时，回溯点多吃一个字符再试。
    /// 复杂度：最坏 O(n·m)，绝不指数爆炸。
    /// </summary>
    internal static bool SimpleGlobMatch(string pattern, string text)
    {
        var p = pattern.AsSpan();
        var t = text.AsSpan();
        int pi = 0, ti = 0;
        int star = -1, starTi = 0;

        while (ti < t.Length)
        {
            if (pi < p.Length && p[pi] == '?')
            {
                pi++; ti++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                // 连续多个 * 等价于一个
                while (pi < p.Length && p[pi] == '*') pi++;
                star = pi;
                starTi = ti;
            }
            else if (pi < p.Length && p[pi] == t[ti])
            {
                pi++; ti++;
            }
            else if (star >= 0)
            {
                // 回溯：* 多吃一个字符
                starTi++;
                ti = starTi;
                pi = star;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}
