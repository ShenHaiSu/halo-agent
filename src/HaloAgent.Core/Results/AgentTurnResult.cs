namespace Host.Desktop.Services;

/// <summary>
/// 单轮 Agent ReAct 执行结果（need21-B03: 自 Services/AgentOrchestrator.cs 移入 Core，语义零变化）。
/// </summary>
public sealed class AgentTurnResult
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public bool WasTruncated { get; init; }
    public int Iterations { get; init; }
    public int ToolExecutions { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AgentTurnResult Completed(int iterations, int toolExecutions) =>
        new()
        {
            Success = true,
            Iterations = iterations,
            ToolExecutions = toolExecutions
        };

    public static AgentTurnResult Truncated(int iterations, int toolExecutions) =>
        new()
        {
            Success = true,
            WasTruncated = true,
            Iterations = iterations,
            ToolExecutions = toolExecutions
        };

    public static AgentTurnResult Cancelled(int iterations, int toolExecutions) =>
        new()
        {
            Success = false,
            WasCancelled = true,
            Iterations = iterations,
            ToolExecutions = toolExecutions
        };

    public static AgentTurnResult Failed(
        string code,
        string message,
        int iterations = 0,
        int toolExecutions = 0
    ) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
            Iterations = iterations,
            ToolExecutions = toolExecutions
        };
}
