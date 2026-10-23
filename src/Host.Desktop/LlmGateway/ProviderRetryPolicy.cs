using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.LlmGateway;

/// <summary>
/// P0-2 重试分池：429 与 transient 分池、服务端 delay 优先、全局封顶、可取消 sleep。
/// 只重试起流前；起流后由轮次级恢复。
/// </summary>
public static class ProviderRetryPolicy
{
    public const int RateLimitedMax = 10;
    public const int TransientMax = 10;
    public const int RateInitialMs = 2000;
    public const int RateCapMs = 30_000;
    public const int TransientCapMs = 8000;

    /// <summary>测试钩子保留（等价保留 RetryDelayAsync）。</summary>
    public static Func<int, CancellationToken, Task> RetryDelayAsync = (ms, ct) =>
        Task.Delay(ms, ct);

    public static int RateLimitDelayMs(int attempt, IReadOnlyDictionary<string, string>? headers)
    {
        int server = ParseServerDelayMs(headers);
        int exp = (int)Math.Min(RateCapMs, RateInitialMs * Math.Pow(2, Math.Max(0, attempt)));
        exp = AddJitter(exp, 0.25);
        if (server > 0)
            return Math.Min(server, RateCapMs);
        return Math.Min(exp, RateCapMs);
    }

    public static int TransientDelayMs(int attempt, IReadOnlyDictionary<string, string>? headers)
    {
        int server = ParseServerDelayMs(headers);
        if (server > 0)
            return Math.Min(server, TransientCapMs);
        int[] table = new int[] { 1000, 2000, 4000, 8000 };
        int idx = Math.Clamp(attempt, 0, table.Length - 1);
        return table[idx];
    }

    private static int ParseServerDelayMs(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null)
            return 0;
        if (
            headers.TryGetValue("retry-after-ms", out var msStr)
            && int.TryParse(msStr, out int ms)
            && ms > 0
        )
            return ms;
        if (headers.TryGetValue("retry-after", out var ra) && !string.IsNullOrWhiteSpace(ra))
        {
            ra = ra.Trim();
            if (int.TryParse(ra, out int sec) && sec >= 0)
                return sec * 1000;
            if (DateTimeOffset.TryParse(ra, out var date))
            {
                int diff = (int)(date - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (diff > 0)
                    return diff;
            }
            // 坏 header 不挂死 turn：返回 0 落到指数退避
        }
        return 0;
    }

    private static int AddJitter(int ms, double ratio)
    {
        int j = (int)(ms * ratio);
        if (j <= 0)
            return ms;
        return ms + Random.Shared.Next(0, j + 1);
    }

    public static Task DelayWithAbortAsync(int ms, CancellationToken ct) => RetryDelayAsync(ms, ct);

    /// <summary>不透明 400 修一次：400/422 + "(no body)" → 去 max_* 重试一次，不耗预算。</summary>
    public static bool ShouldStripMaxTokensOnce(int status, string? body) =>
        (status == 400 || status == 422)
        && (body ?? string.Empty).Contains("(no body)", StringComparison.Ordinal);

    public static string StripMaxTokens(string payload)
    {
        try
        {
            string s = System.Text.RegularExpressions.Regex.Replace(
                payload,
                @",\s*""max_(tokens|completion_tokens|output_tokens)""\s*:\s*\d+",
                string.Empty
            );
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"""max_(tokens|completion_tokens|output_tokens)""\s*:\s*\d+\s*,",
                string.Empty
            );
            return s;
        }
        catch
        {
            return payload;
        }
    }
}
