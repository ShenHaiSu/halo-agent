using System.Text;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools;

/// <summary>
/// 统一工具注册调度中心：注册 15 个标准基础工具（6 存量 + view_image/ask_user/skill_tool/memory_tool + need13 spawn_subagent + need14 edit_lines + need15 undo_edit/edit + need16 goal_tool），
/// DispatchAsync 捕获异常转自愈提示，输出统一过 ToolOutputGuard，
/// 支持 GetDefinitions() 导出 chat/resp 双协议 Schema。
/// 并发安全（无共享可变状态，处理器均为无状态）。
/// </summary>
public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registerLock = new();
    private readonly ToolPipeline _pipeline = new();

    public ToolRegistry(bool registerBuiltIns = true)
    {
        if (registerBuiltIns)
        {
            RegisterAttributedTools();
        }
    }

    /// <summary>
    /// 注册工具处理器（同名覆盖）。
    /// </summary>
    public void Register(IToolHandler handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_registerLock)
        {
            _handlers[handler.Name] = handler;
        }
    }

    /// <summary>need21-B04: [Tool] 扫描注册（新增工具只加 1 文件）。同名覆盖；无参构造失败则跳过。</summary>
    public void RegisterAttributedTools()
    {
        var asm = typeof(ToolRegistry).Assembly;
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;
            var attr = type.GetCustomAttributes(
                typeof(HaloAgent.Tools.ToolAttribute),
                inherit: false
            );
            if (attr.Length == 0)
                continue;
            if (!typeof(IToolHandler).IsAssignableFrom(type))
                continue;
            try
            {
                var inst = System.Activator.CreateInstance(type) as IToolHandler;
                if (inst != null)
                    Register(inst);
            }
            catch { }
        }
    }

    /// <summary>
    /// 已注册的工具名列表。
    /// </summary>
    public IReadOnlyList<string> ToolNames
    {
        get
        {
            lock (_registerLock)
            {
                return _handlers.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// 导出全部工具定义（按 name 升序稳定排序，need09-07 新工具自动归位）。
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetDefinitions()
    {
        lock (_registerLock)
        {
            return _handlers
                .Values.Select(h =>
                {
                    try
                    {
                        return h.GetDefinition();
                    }
                    catch (Exception)
                    {
                        return new ToolDefinition(
                            h.Name,
                            h.Description,
                            "{\"type\":\"object\",\"properties\":{}}"
                        );
                    }
                })
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// need17-01/联调：导出激活工具定义。Hidden/Disabled 永不声明（01 冻结：edit 可执行、可转调，但不声明）；
    /// active==null 视为全部 Direct。Orchestrator 在轮首计算 activeSet、本轮冻结后传入。
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetActiveDefinitions(ISet<string>? active)
    {
        var all = GetDefinitions();
        HashSet<string>? activeSet =
            active != null
                ? new HashSet<string>(
                    active.Where(n => !string.IsNullOrWhiteSpace(n)),
                    StringComparer.OrdinalIgnoreCase
                )
                : null;
        return all.Where(d =>
            {
                if (d == null || d.Exposure != ToolExposure.Direct)
                {
                    return false;
                }
                if (activeSet != null)
                {
                    return activeSet.Contains(d.Name);
                }
                return true;
            })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 调度执行：未知工具与异常统一转为自然语言自愈提示，成功输出统一过 ToolOutputGuard。
    /// need17-02：老签名保留（activeTools=null=不做激活过滤，仅中央校验；老调用零改动）。
    /// </summary>
    public Task<ToolResult> DispatchAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    ) => DispatchAsync(call, context, null, ct);

    public Task<ToolResult> DispatchAsync(
        ToolCallDto call,
        ToolContext context,
        ISet<string>? activeTools,
        CancellationToken ct = default,
        bool closedWorld = true
    ) =>
        _pipeline.ExecuteAsync(
            call,
            context,
            activeTools,
            ct,
            closedWorld,
            ResolveHandler,
            () => string.Join("、", ToolNames)
        );

    private IToolHandler? ResolveHandler(string name)
    {
        lock (_registerLock)
        {
            _handlers.TryGetValue(name, out var h);
            return h;
        }
    }
}
