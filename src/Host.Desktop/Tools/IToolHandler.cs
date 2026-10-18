using Host.Desktop.Tools.Process;

namespace Host.Desktop.Tools;

/// <summary>
/// 单次工具调用请求（对应 LLM 的 tool_call）。
/// </summary>
public sealed class ToolCallDto
{
    public ToolCallDto()
    {
        Id = string.Empty;
        Name = string.Empty;
        ArgumentsJson = "{}";
    }

    public ToolCallDto(string id, string name, string? argumentsJson = null)
    {
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
    }

    public string Id { get; set; }
    public string Name { get; set; }
    public string ArgumentsJson { get; set; }
}

/// <summary>
/// 单次工具执行的上下文：工作区根、会话归属、数据根与可覆盖的 Python 探测。
/// </summary>
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string DataRoot { get; init; } = string.Empty;

    /// <summary>
    /// 用户在设置中心显式指定的 Python 路径（可空）。
    /// </summary>
    public string? ConfiguredPythonPath { get; init; }

    /// <summary>
    /// 测试/上层注入的 Python 探测覆盖（为空则走真实探测）。
    /// </summary>
    public Func<CommandRunnerProbe.ProbeResult>? PythonProbeOverride { get; init; }
    /// <summary>
    /// need17-07：Python 可用性快照覆盖（单测/子代理注入；生产为 null→读静态快照）。
    /// </summary>
    public Host.Desktop.Tools.Process.PythonAvailabilitySnapshot? PythonAvailabilityOverride { get; init; }

    /// <summary>
    /// need08：命令超时配置快照（null=读服务/常量回退，保证老单测行为不变）。
    /// 由 AgentOrchestrator 每次调度前填入；HostBridgeFacade.ExecuteTool 现场读取填入。
    /// </summary>
    public int? ExecDefaultTimeoutMsOverride { get; set; }
    public int? ExecMaxTimeoutMsOverride { get; set; }
    /// <summary>need13：main|sub（默认 main，老调用零改动）。</summary>
    public string AgentRole { get; init; } = "main";
    /// <summary>need13：sub 寄宿的主会话（main 为 null）。</summary>
    public string? ParentSessionId { get; init; }
    /// <summary>need13：轮首策略快照（见 05 §3，Orchestrator 每轮填一次）。</summary>
    public bool SubAgentEnabled { get; set; } = true;
    public int SubMaxParallel { get; set; } = 3;
    public int SubMaxIterations { get; set; } = 15;
    public bool SubAllowExecCommand { get; set; } = true;
    public bool SubAllowFileWrite { get; set; } = true;
    public bool SubAllowSkillWrite { get; set; } = false;
    public bool SubAllowMemoryWrite { get; set; } = false;
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
    /// <summary>need09-07：skill/memory 工具默认写域（模型不填 scope 即写 Project，防污染全局）。</summary>
    public string SkillMemoryScopeDefault { get; init; } = "project";
    /// <summary>
    /// 会话临时目录：&lt;DataRoot&gt;/sessions/&lt;ws&gt;/&lt;sess&gt;/tmp
    /// </summary>
    public string SessionTempDir =>
        Path.Combine(
            string.IsNullOrWhiteSpace(DataRoot) ? Path.GetTempPath() : DataRoot,
            "sessions", WorkspaceId, SessionId, "tmp");
}

/// <summary>
/// 标准工具处理器接口：所有供 LLM 调用的基础工具统一实现此契约。
/// </summary>
public interface IToolHandler
{
    /// <summary>工具名称（如 exec_command）。</summary>
    string Name { get; }

    /// <summary>工具人类可读描述（同时写入 LLM Schema）。</summary>
    string Description { get; }

    /// <summary>返回工具定义（含 JSON Schema 参数表）。</summary>
    ToolDefinition GetDefinition();

    /// <summary>
    /// 执行工具。实现方不得抛出异常（异常由 ToolRegistry 统一捕获转自愈提示）。
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default);
}
