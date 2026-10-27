using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools.Builtin;

public enum MemoryScope { Global, Project }

public static class MemoryScopeExtensions
{
    public static string ToWire(this MemoryScope s) => s == MemoryScope.Global ? "global" : "project";
    public static string ToLabel(this MemoryScope s) => s == MemoryScope.Global ? "全局" : "项目";

    public static bool TryParse(string? raw, out MemoryScope scope)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "" or null or "global" or "user": scope = MemoryScope.Global; return true;
            case "project" or "repo": scope = MemoryScope.Project; return true;
            default: scope = MemoryScope.Global; return false;
        }
    }
}

/// <summary>
/// 长期记忆（两级）。
///
/// ============ 目录 ============
///   Global:  %USERPROFILE%\.haloagent\memories\
///   Project: &lt;cwd&gt;\.haloagent\memories\   （按需创建，不在每个目录留空壳）
///
/// ============ 索引 ============
///   每级一个 MEMORY.md，一行一条：
///     - [name](slug.md) — description
///
/// ============ 安全 ============
///   slugify 只放行字母数字 - _；且最终路径的父目录必须就是作用域目录
///   （双保险，防未来有人放宽 slugify 导致路径穿越）
/// </summary>
public sealed class MemoryStore
{
    private readonly string _globalDir;
    private readonly string _projectDir;

    public MemoryStore(string dataDir, string cwd)
    {
        _globalDir = Path.Combine(dataDir, "memories");
        _projectDir = Path.Combine(cwd, ".haloagent", "memories");
    }

    public string DirFor(MemoryScope scope) =>
        scope == MemoryScope.Global ? _globalDir : _projectDir;

    public sealed record MemoryFile(string Filename, string Name, string Description,
        string Mtype, MemoryScope Scope, string Body, long UpdatedMs);

    public static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "memory";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    public async Task<ToolResult> WriteAsync(
        string name, string content, string? description, string? mtype,
        MemoryScope scope, CancellationToken ct)
    {
        var dir = DirFor(scope);
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { return ToolResult.Failure($"创建记忆目录失败: {ex.Message}"); }

        var slug = Slugify(name);
        var path = Path.Combine(dir, slug + ".md");

        // 双保险：确认路径没跑出作用域目录
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(Path.GetFullPath(parent!), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failure($"记忆名不合法: {name}");

        var desc = string.IsNullOrWhiteSpace(description) ? $"关于「{name}」的记忆" : description!;
        var type = string.IsNullOrWhiteSpace(mtype) ? "project" : mtype!;

        var body = $"---\nname: {name}\ndescription: {desc}\ntype: {type}\n---\n\n{content}";
        try { await File.WriteAllTextAsync(path, body, Encoding.UTF8, ct).ConfigureAwait(false); }
        catch (Exception ex) { return ToolResult.Failure($"写入记忆失败: {ex.Message}"); }

        await UpdateIndexAsync(dir, name, desc, slug, ct).ConfigureAwait(false);

        var where = scope == MemoryScope.Project ? $"\n位置: {dir}\n" : "";
        return ToolResult.Success(
            $"已保存{scope.ToLabel()}记忆「{name}」(共 {content.Length} 字)。索引已更新。{where}");
    }

    private static async Task UpdateIndexAsync(
        string dir, string name, string description, string slug, CancellationToken ct)
    {
        var indexPath = Path.Combine(dir, "MEMORY.md");
        var line = $"- [{name}]({slug}.md) — {description}";

        var lines = new List<string>();
        if (File.Exists(indexPath))
        {
            try { lines = (await File.ReadAllLinesAsync(indexPath, ct).ConfigureAwait(false)).ToList(); }
            catch { }
        }
        if (lines.Count == 0) lines.Add("# 记忆索引\n");

        // 去掉同名旧行（防重复）
        lines.RemoveAll(l => l.Contains($"]({slug}.md)", StringComparison.OrdinalIgnoreCase));
        lines.Add(line);

        try { await File.WriteAllLinesAsync(indexPath, lines, ct).ConfigureAwait(false); }
        catch { /* 索引更新失败不影响记忆本身 */ }
    }

    public async Task<List<MemoryFile>> ListAsync(CancellationToken ct)
    {
        var result = new List<MemoryFile>();
        foreach (var scope in new[] { MemoryScope.Project, MemoryScope.Global })
        {
            var dir = DirFor(scope);
            if (!Directory.Exists(dir)) continue;

            foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(f).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var text = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
                    var (fm, body) = ParseFrontmatter(text);
                    result.Add(new MemoryFile(
                        Filename: Path.GetFileNameWithoutExtension(f),
                        Name: fm.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(f)),
                        Description: fm.GetValueOrDefault("description", ""),
                        Mtype: fm.GetValueOrDefault("type", ""),
                        Scope: scope,
                        Body: body,
                        UpdatedMs: new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds()));
                }
                catch { }
            }
        }
        return result;
    }

    public async Task<MemoryFile?> ReadAsync(
        string filename, MemoryScope scope, CancellationToken ct)
    {
        var dir = DirFor(scope);
        var path = Path.Combine(dir, Slugify(filename) + ".md");
        if (!File.Exists(path))
        {
            // 也允许直接传文件名
            path = Path.Combine(dir, filename + ".md");
            if (!File.Exists(path)) return null;
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var (fm, body) = ParseFrontmatter(text);
        return new MemoryFile(
            Path.GetFileNameWithoutExtension(path),
            fm.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(path)),
            fm.GetValueOrDefault("description", ""),
            fm.GetValueOrDefault("type", ""),
            scope, body,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds());
    }

    private static (Dictionary<string, string> fm, string body) ParseFrontmatter(string text)
    {
        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---")) return (fm, text.Trim());

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (fm, text.Trim());

        var header = text[3..end];
        var body = text[(end + 4)..].Trim();

        foreach (var line in header.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0) fm[key] = val;
        }
        return (fm, body);
    }
}

public sealed class MemoryWriteTool : ITool
{
    public string Name => "memory_write";
    public string Description =>
        "写入一条长期记忆(跨会话保留)。记忆分两级:Global(随用户走,记个人偏好/机器环境)" +
        "与 Project(随当前仓库走,记本仓库的约定/架构/待办);scope 缺省 global。" +
        "适合记录:用户机器环境、常用配置/路径、用户偏好、重要决策、本仓库约定。" +
        "内容用 markdown,第一句是结论";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""name"":        { ""type"": ""string"", ""description"": ""记忆名称(短,如 '机器硬件配置')"" },
        ""content"":     { ""type"": ""string"", ""description"": ""记忆正文(markdown)"" },
        ""description"": { ""type"": ""string"", ""description"": ""一句话描述(用于索引,可选)"" },
        ""mtype"":       { ""type"": ""string"", ""description"": ""分类标签:user/project/reference/feedback,缺省 project(只是分类,不决定存哪一级)"" },
        ""scope"":       { ""type"": ""string"", ""enum"": [""global"",""project""], ""description"": ""存哪一级:project = 本仓库的约定/待办(随仓库走);global = 个人偏好/机器环境(随用户走)。缺省 global"" }
      },
      ""required"": [""name"",""content""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.WriteFile;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var name = args.GetStringOrNull("name");
        var content = args.GetStringOrNull("content");
        if (string.IsNullOrEmpty(name)) return ToolResult.Failure("name 不能为空");
        if (content is null) return ToolResult.Failure("content 不能为空");

        var scopeRaw = args.GetStringOrNull("scope");
        if (!MemoryScopeExtensions.TryParse(scopeRaw, out var scope))
            return ToolResult.Failure(
                $"scope 只能是 global 或 project(收到「{scopeRaw}」)。" +
                "个人偏好/机器环境用 global,本仓库的约定用 project");

        var store = new MemoryStore(ctx.Host.DataDir, ctx.Cwd);
        return await store.WriteAsync(
            name, content,
            args.GetStringOrNull("description"),
            args.GetStringOrNull("mtype"),
            scope, ct).ConfigureAwait(false);
    }
}

public sealed class MemoryReadTool : ITool
{
    public string Name => "memory_read";
    public string Description =>
        "读取长期记忆。可按名字精确读取,或按关键词搜索。会话开始已自动注入两级的 MEMORY.md 索引摘要。" +
        "缺省两级都读:同名的两条都会返回并标注来源(项目在前)";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""query"": { ""type"": ""string"", ""description"": ""关键词搜索(可选)"" },
        ""name"":  { ""type"": ""string"", ""description"": ""精确读取某条记忆的名字(可选)"" },
        ""scope"": { ""type"": ""string"", ""enum"": [""global"",""project"",""all""], ""description"": ""只读哪一级;缺省或 all = 两级都读(结果按来源分组)"" }
      },
      ""required"": []
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var store = new MemoryStore(ctx.Host.DataDir, ctx.Cwd);
        var query = args.GetStringOrNull("query");
        var name = args.GetStringOrNull("name");

        var all = await store.ListAsync(ct).ConfigureAwait(false);

        // 按名字精确读
        if (!string.IsNullOrWhiteSpace(name))
        {
            var slug = MemoryStore.Slugify(name);
            var hits = all.Where(m => m.Filename.Equals(slug, StringComparison.OrdinalIgnoreCase)
                                   || m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) return ToolResult.Success($"未找到名为「{name}」的记忆");

            var sb = new StringBuilder();
            foreach (var h in hits)
                sb.Append($"## {h.Name}（{h.Scope.ToLabel()}）\n{h.Body}\n\n");
            return ToolResult.Success(sb.ToString());
        }

        // 按关键词搜索
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var hits = all.Where(m =>
                m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Body.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            if (hits.Count == 0) return ToolResult.Success($"没有匹配「{q}」的记忆");

            var sb = new StringBuilder();
            foreach (var group in hits.GroupBy(h => h.Scope))
            {
                sb.Append($"## {group.Key.ToLabel()}记忆\n");
                foreach (var h in group)
                    sb.Append($"- {h.Name}: {h.Description}\n");
                sb.Append('\n');
            }
            return ToolResult.Success(sb.ToString());
        }

        // 都读：索引
        if (all.Count == 0) return ToolResult.Success("(暂无记忆)");

        var list = new StringBuilder("记忆索引（项目在前）:\n");
        foreach (var group in all.GroupBy(h => h.Scope))
        {
            list.Append($"\n### {group.Key.ToLabel()}\n");
            foreach (var h in group)
                list.Append($"- {h.Name} — {h.Description}\n");
        }
        list.Append("\n(读全文请在 name 参数里传记忆名)");
        return ToolResult.Success(list.ToString());
    }
}
