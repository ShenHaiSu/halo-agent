namespace Host.Desktop.Tools.Hooks;

/// <summary>
/// need17-06 §6.5：工具前/后置钩子插槽（权限/脱敏/审计预留）。
/// 本期只定接口 + 默认透传实现，零行为变化；DispatchAsync 接入由基础组 02 落子。
/// </summary>
public interface IToolHook
{
    /// <summary>
    /// Validator 通过后、执行前调用。返回 Blocked 则跳过执行，
    /// 直接返回 <c>ToolResult.FailCode("TOOL_BLOCKED", reason)</c>。
    /// </summary>
    Task<ToolHookDecision> BeforeAsync(ToolCallDto call, string normalizedArgsJson, ToolContext ctx, CancellationToken ct);

    /// <summary>
    /// 执行后调用。整字段覆盖语义：返回值的 <c>Output/Error/Success</c> 三字段直接覆盖原结果。
    /// </summary>
    Task<ToolResult> AfterAsync(ToolCallDto call, ToolResult result, ToolContext ctx, CancellationToken ct);
}

/// <summary>
/// need17-06 §6.5：钩子前置决策。Proceed=放行；Blocked=拦截并以 reason 拒绝（码 TOOL_BLOCKED）。
/// </summary>
public sealed class ToolHookDecision
{
    private ToolHookDecision(bool blocked, string? reason)
    {
        Blocked = blocked;
        Reason = reason ?? string.Empty;
    }

    public bool Blocked { get; }
    public string Reason { get; }

    public static ToolHookDecision Proceed() => new(false, null);

    public static ToolHookDecision Block(string reason) => new(true, reason ?? string.Empty);
}
