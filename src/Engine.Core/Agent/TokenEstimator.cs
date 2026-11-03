namespace HaloAgent.Engine.Core.Agent;

/// <summary>
/// P23：确定性字符估算（非分词器）。
/// 公式冻结：EstimateTokens(text) = max(1, ceil(utf16Length / 4))，无超长钳制。
/// 误差由前端“预估”角标覆盖，禁止为此引入分词器。
/// </summary>
public static class TokenEstimator
{
    public static long Estimate(string? text)
    {
        var len = text?.Length ?? 0;
        var v = (len + 3) / 4;
        return Math.Max(1, v);
    }
}
