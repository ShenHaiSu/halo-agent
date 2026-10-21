namespace Host.Desktop.Tools.Hooks;

/// <summary>
/// need17-06 §6.5：默认透传钩子（默认注册）。Before 永远放行，After 原样返回，零行为变化。
/// 前端审计/密钥脱敏留给 need18；本期单测只断言透传。
/// </summary>
public sealed class PassThroughToolHook : IToolHook
{
    public static readonly PassThroughToolHook Instance = new();

    public Task<ToolHookDecision> BeforeAsync(
        ToolCallDto call,
        string normalizedArgsJson,
        ToolContext ctx,
        CancellationToken ct
    ) => Task.FromResult(ToolHookDecision.Proceed());

    public Task<ToolResult> AfterAsync(
        ToolCallDto call,
        ToolResult result,
        ToolContext ctx,
        CancellationToken ct
    ) => Task.FromResult(result ?? ToolResult.Fail($"工具 '{call?.Name}' 返回空结果，请重试。"));
}
