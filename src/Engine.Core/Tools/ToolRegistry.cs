using HaloAgent.Engine.Core.Tools.Builtin;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 工具注册表。
///
/// ============ 顺序即 UI 顺序 ============
/// 前端工具列表按这里的顺序展示（读 → 写 → 执行 → 记忆 → 网络）。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
    private readonly List<ITool> _ordered = new();

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var t in tools)
        {
            if (!_tools.TryAdd(t.Name, t))
                throw new InvalidOperationException($"工具重复注册: {t.Name}");
            _ordered.Add(t);
        }
    }

    public static ToolRegistry CreateBuiltin() => new(new ITool[]
    {
        new ListDirectoryTool(),
        new ReadFileTool(),
        new WriteFileTool(),
        new EditTool(),
        new GlobTool(),
        new GrepSearchTool(),
        new RunShellCommandTool(),
        new MemoryWriteTool(),
        new MemoryReadTool(),
        new WebFetchTool(),
    });

    public IReadOnlyList<ITool> All => _ordered;
    public IEnumerable<string> Names => _ordered.Select(t => t.Name);
    public ITool? Get(string name) => _tools.GetValueOrDefault(name);
    public bool Has(string name) => _tools.ContainsKey(name);

    /// <summary>生成给模型看的工具定义列表（OpenAI function calling 格式）。</summary>
    public List<object> ToToolDefs()
    {
        var defs = new List<object>(_ordered.Count);
        foreach (var t in _ordered)
        {
            defs.Add(new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParameterSchema,
                },
            });
        }
        return defs;
    }
}
