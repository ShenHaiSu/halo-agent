using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need13：spawn_subagent（仅 Main registry 注册；实际执行由 Orchestrator 分流承接，见 04 §3）。
/// 此处 ExecuteAsync 只做兜底（防 HostBridgeFacade.ExecuteTool 单跑误调）。
/// </summary>
[HaloAgent.Tools.Tool("spawn_subagent")]
public sealed class SpawnSubAgentTool : IToolHandler
{
    private readonly bool _enabled;

    public SpawnSubAgentTool()
        : this(enabled: true) { }

    public SpawnSubAgentTool(bool enabled = true)
    {
        _enabled = enabled;
    }

    // NEED17-05 §5.1: Exposure=Direct（主包常显现状保持；ForSub 禁用桩不动；激活集裁剪由基础组 01 落子）。
    public string Name => "spawn_subagent";
    public const string Need17Exposure = "Direct";

    // NEED17-05 §5.3: 供 06 动态装配的 PromptBrief/PromptRules（ToolDefinition 字段待基础组 01 扩展，此处先以常量承载，装配表见 SystemPromptBuilder）。
    public const string PromptBrief = "Spawn a clean-context sub-agent for independent work";
    public static readonly string[] PromptRules =
    {
        "Include full background in task (sub sees no main history)."
    };

    public string Description =>
        "Spawn a clean-context sub-agent under current session. "
        + "Use for parallelizable independent work. "
        + "The sub cannot see main history (pass all background in 'task'), "
        + "has read-only skills/memory, cannot ask user, cannot spawn further subs. "
        + "Include full background in task (sub sees no main history). Sub returns a summary; barrier waits for all."
        + (_enabled ? string.Empty : " [当前已禁用：设置>智能体行为>子代理>总开关=false，调用将直接拒绝]");

    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            new ToolSchemaBuilder()
                .AddString(
                    "task",
                    "Sub task prompt. Include goal, scope paths, constraints, expected output format. Required, non-empty.",
                    required: true,
                    minLength: 1
                )
                .AddString("label", "Short label for UI (<=20 chars). Optional.", maxLength: 20)
                .AddString("role", "Optional sub role hint.")
                .AddString(
                    "model",
                    "Optional model id override for sub (must be enabled). Defaults to main model."
                )
                .AddString("providerId", "Optional provider id override for sub.")
                .AddInteger(
                    "maxIterations",
                    "Sub turn cap, 1..50, default 15.",
                    minimum: 1,
                    maximum: 50
                )
                .Build()
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            ToolResult.Fail("[SUBAGENT_INTERNAL] spawn 由 Orchestrator barrier 承接，不应直调。")
        );
}

/// <summary>need13：仅 Sub registry 的禁用桩（直接拒，语义最清晰）。</summary>
public sealed class DisabledSpawnTool : IToolHandler
{
    public string Name => "spawn_subagent";
    public string Description => "Disabled in sub-agent (depth=1). Calls are rejected.";

    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            "{\"type\":\"object\",\"properties\":{\"task\":{\"type\":\"string\"}},\"required\":[\"task\"]}"
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            ToolResult.Fail(
                "[SUBAGENT_NESTED_DENIED] 子代理内禁止再 spawn（深度=1）。本次已拒绝。请在当前子内串行完成，必要时记入总结交主代理拆分。"
            )
        );
}

/// <summary>need13：仅 Sub registry 的 ask 禁用桩（首行拒，未进 gate）。</summary>
public sealed class DisabledAskUserTool : IToolHandler
{
    public string Name => "ask_user";
    public string Description => "Disabled in sub-agent (v1 locked). Calls are rejected.";

    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            "{\"type\":\"object\",\"properties\":{\"questions\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}},\"required\":[]}"
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return Task.FromResult(
            ToolResult.Fail(
                "[SUBAGENT_ASK_DENIED] 子代理禁止 ask_user（v1 锁定）。本次已拒绝，不会弹窗。请按保守默认继续，并在总结中注明假设，交用户在主会话确认。",
                durationMs: sw.ElapsedMilliseconds
            )
        );
    }
}
