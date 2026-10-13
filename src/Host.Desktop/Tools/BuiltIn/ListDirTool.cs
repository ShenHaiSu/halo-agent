using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// list_dir：工作区目录结构浏览。自动排除构建产物与巨型依赖目录。
/// </summary>
public sealed class ListDirTool : IToolHandler
{
    public const int DefaultMaxDepth = 2;
    public const int MaxAllowedDepth = 10;

    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "__pycache__", ".vs",
    };

    public string Name => "list_dir";

    public string Description =>
        "List directory structure in the workspace with file sizes. " +
        "Supports recursion depth control; noisy directories (.git, node_modules, bin, obj, ...) are auto-excluded.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"path\":{\"type\":\"string\",\"description\":\"Relative directory path from workspace root or absolute path. Defaults to \\\".\\\".\"},"
        + "\"recursive\":{\"type\":\"boolean\",\"description\":\"Recurse into subdirectories. Defaults to false.\"},"
        + "\"max_depth\":{\"type\":\"integer\",\"description\":\"Maximum recursion depth. Defaults to 2.\"}"
        + "}}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? pathArg = null;
            bool recursive = false;
            int maxDepth = DefaultMaxDepth;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("path", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String)
                {
                    pathArg = pEl.GetString();
                }

                if (root.TryGetProperty("recursive", out JsonElement rEl) &&
                    (rEl.ValueKind == JsonValueKind.True || rEl.ValueKind == JsonValueKind.False))
                {
                    recursive = rEl.GetBoolean();
                }

                if (root.TryGetProperty("max_depth", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Number
                    && dEl.TryGetInt32(out int parsedDepth))
                {
                    maxDepth = parsedDepth;
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 path/recursive/max_depth 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (maxDepth < 1)
            {
                maxDepth = DefaultMaxDepth;
            }

            maxDepth = Math.Min(maxDepth, MaxAllowedDepth);
            int effectiveDepth = recursive ? maxDepth : 1;

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            string fullPath = PathSafetyValidator.ResolveWorkspacePath(workspaceRoot, string.IsNullOrWhiteSpace(pathArg) ? "." : pathArg);

            if (!Directory.Exists(fullPath))
            {
                if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    return Task.FromResult(ToolResult.Ok(
                        $"路径 '{fullPath}' 是文件而非目录（{fi.Length} 字节）。请用 read_file 读取。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                return Task.FromResult(ToolResult.Fail(
                    $"目录不存在：'{fullPath}'。请检查 path 参数后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            var sb = new StringBuilder();
            sb.Append("目录树：").Append(fullPath)
              .Append("（深度≤").Append(effectiveDepth).Append(')');
            int dirCount = 0;
            int fileCount = 0;

            try
            {
                Walk(fullPath, 0, effectiveDepth, sb, ref dirCount, ref fileCount);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"目录遍历失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            sb.AppendLine().Append($"共 {dirCount} 个目录，{fileCount} 个文件。");
            return Task.FromResult(ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"list_dir 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }

    private static void Walk(
        string dir,
        int depth,
        int maxDepth,
        StringBuilder sb,
        ref int dirCount,
        ref int fileCount)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception)
        {
            sb.AppendLine().Append(new string(' ', depth * 2)).Append("[无权访问]");
            return;
        }

        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

        var dirs = new List<string>();
        var files = new List<string>();
        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            try
            {
                if (Directory.Exists(entry))
                {
                    if (ExcludedDirNames.Contains(name))
                    {
                        continue;
                    }

                    dirs.Add(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
            catch (Exception)
            {
                // 忽略单个条目判定异常
            }
        }

        string indent = new string(' ', depth * 2);
        foreach (string subDir in dirs)
        {
            dirCount++;
            sb.AppendLine().Append(indent).Append("├─ ").Append(Path.GetFileName(subDir)).Append('/');
            if (depth + 1 < maxDepth)
            {
                Walk(subDir, depth + 1, maxDepth, sb, ref dirCount, ref fileCount);
            }
        }

        foreach (string file in files)
        {
            fileCount++;
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                size = -1;
            }

            sb.AppendLine().Append(indent).Append("├─ ").Append(Path.GetFileName(file));
            sb.Append(size >= 0 ? $" ({size} bytes)" : " (大小未知)");
        }
    }
}
