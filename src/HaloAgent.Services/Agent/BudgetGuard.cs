namespace HaloAgent.Services.Agent;

/// <summary>
/// need21-B03: Token 预算守卫（纯函数，可单测，无外部依赖）。
/// 回答 go/no-go，不碰 LLM/工具/历史；Host.Desktop 侧调度器每轮调用。
/// </summary>
public static class BudgetGuard
{
    public const double HighWaterMarkRatio = 0.92;

    public static bool ShouldTruncate(int usedTokens, int budgetTokens) =>
        budgetTokens > 0 && usedTokens >= budgetTokens;

    public static bool IsHighWater(int usedTokens, int budgetTokens) =>
        budgetTokens > 0 && usedTokens >= (int)(budgetTokens * HighWaterMarkRatio);

    public static bool ShouldStopOnRepeatedError(int consecutiveSameError, int threshold = 3) =>
        consecutiveSameError >= threshold;

    public static bool IsInfinite(int effectiveMax) => effectiveMax <= 0;

    public static bool ReachedMaxIterations(int iteration, int effectiveMax) =>
        !IsInfinite(effectiveMax) && iteration > effectiveMax;
}
