using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-2：输出上限 OutputCap。
/// EstimateOutputCapInput = systemPrompt + tool schemas + hosted-search 投影；
/// contextBudget = max(calibratedMessages, outputCapInput)。
/// </summary>
public static class OutputCap
{
    /// <summary>
    /// 输出上限输入估算 = system 文本 + 工具 schema 投影 + hosted-search 投影。
    /// </summary>
    public static int EstimateOutputCapInput(
        string? systemPrompt,
        IReadOnlyList<UnifiedToolDefinition>? tools,
        string? hostedSearchProjection = null
    )
    {
        long total = TokenBudgetService.EstimateTokens(systemPrompt);
        if (tools != null)
        {
            foreach (var t in tools)
            {
                if (t == null)
                    continue;
                total += TokenBudgetService.EstimateTokens(t.Name);
                total += TokenBudgetService.EstimateTokens(t.Description);
                try
                {
                    total += TokenBudgetService.EstimateTokens(t.ParametersSchema.GetRawText());
                }
                catch
                {
                    // schema 不可读则按 0 计，不中断预算。
                }
            }
        }
        total += TokenBudgetService.EstimateTokens(hostedSearchProjection);
        return (int)Math.Min(int.MaxValue, Math.Max(0, total));
    }

    /// <summary>contextBudget = max(calibratedMessages, outputCapInput)。</summary>
    public static int ContextBudget(int calibratedMessages, int outputCapInput) =>
        Math.Max(Math.Max(1, calibratedMessages), Math.Max(1, outputCapInput));

    /// <summary>一站式：校准值与输出上限输入取 max。</summary>
    public static int ContextBudgetWithCap(
        int calibratedMessages,
        string? systemPrompt,
        IReadOnlyList<UnifiedToolDefinition>? tools,
        string? hostedSearchProjection = null
    ) =>
        ContextBudget(
            calibratedMessages,
            EstimateOutputCapInput(systemPrompt, tools, hostedSearchProjection)
        );
}
