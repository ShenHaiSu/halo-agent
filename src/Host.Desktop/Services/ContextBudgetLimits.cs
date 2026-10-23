using System;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-2：上下文预算公式（全部从 window 推导，无魔法阈值；父子同公式）。
/// window = model.MaxContextTokens ?? 128000
/// outputBudget = min(model.MaxOutputTokens ?? 8192, window*0.25)
/// reserveFloor = min(16384, window*0.5)
/// headroom = min(window-1, max(reserveFloor, outputBudget, window*0.05))
/// hardLimit = window - headroom
/// keepRecent = clamp(hardLimit*0.2, 8000, 64000) 且 ≤ hardLimit*0.5
/// autoThreshold = hardLimit*0.9
/// retainedUser ≤ min(20000, hardLimit*0.5)
/// 病态小窗钳 ≥1。
/// </summary>
public sealed record ContextBudgetLimits(
    int Window,
    int OutputBudget,
    int ReserveFloor,
    int Headroom,
    int HardLimit,
    int KeepRecent,
    int AutoThreshold,
    int RetainedUser
)
{
    public static ContextBudgetLimits FromModel(ModelConfig? model) =>
        FromWindow(
            model != null && model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000,
            model != null && model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 8192
        );

    public static ContextBudgetLimits FromWindow(int window, int maxOutputTokens)
    {
        if (window < 1)
            window = 1;
        if (maxOutputTokens < 1)
            maxOutputTokens = 1;
        int outputBudget = Math.Min(maxOutputTokens, (int)(window * 0.25));
        outputBudget = Math.Max(1, outputBudget);
        int reserveFloor = Math.Min(16384, (int)(window * 0.5));
        reserveFloor = Math.Max(1, reserveFloor);
        int headroom = Math.Max(reserveFloor, Math.Max(outputBudget, (int)(window * 0.05)));
        headroom = Math.Max(1, Math.Min(window - 1 < 1 ? 1 : window - 1, headroom));
        int hardLimit = Math.Max(1, window - headroom);
        int keepRecent = (int)(hardLimit * 0.2);
        keepRecent = Math.Clamp(keepRecent, 8000, 64000);
        keepRecent = Math.Min(keepRecent, Math.Max(1, (int)(hardLimit * 0.5)));
        keepRecent = Math.Max(1, keepRecent);
        int autoThreshold = Math.Max(1, (int)(hardLimit * 0.9));
        int retainedUser = Math.Min(20000, (int)(hardLimit * 0.5));
        retainedUser = Math.Max(1, retainedUser);
        return new ContextBudgetLimits(
            window,
            outputBudget,
            reserveFloor,
            headroom,
            hardLimit,
            keepRecent,
            autoThreshold,
            retainedUser
        );
    }
}
