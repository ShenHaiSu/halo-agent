using System.Net.Sockets;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// 弱网重试政策。
///
/// ============ 三条铁律 ============
/// 1. **只重试一次模型调用**，不重跑工具。有副作用的工具（写文件、跑命令）绝不能因重试而执行两次。
/// 2. 判定**结构化**（异常类型 + 状态码），不看错误文本。
///    文本里恰好出现 "5000" 的 400 不该被当成 500。
/// 3. 退避期间按 Esc 可中断。
///
/// ============ 判定表 ============
/// 重试：连接失败 / 超时 / 响应中断 / 429 / 500 / 502 / 503 / 504 / 空回复
/// 不重试：400 / 401 / 403 / 404 / 422 / 截断(length) / 用户取消
/// </summary>
public static class RetryPolicy
{
    public const int MaxRetry = 8;
    private const int BaseBackoffMs = 800;
    private const int MaxBackoffMs = 8_000;

    /// <summary>这次失败值不值得重试？返回 Some(人读原因) 表示值得。</summary>
    public static string? GetRetryReason(Exception ex)
    {
        if (ex is EndpointStatusException ese)
        {
            return ese.Status switch
            {
                429 => "服务端限流(429)",
                500 => "服务端错误(500)",
                502 => "网关错误(502)",
                503 => "服务不可用(503)",
                504 => "网关超时(504)",
                _ => null,
            };
        }

        if (ex is OperationCanceledException) return null;   // 用户取消 / 超时归 HttpRequestException

        if (ex is HttpRequestException hre)
        {
            if (hre.InnerException is SocketException) return "连接失败";
            if (hre.InnerException is TimeoutException) return "连接超时";
            return "连接失败";
        }

        if (ex is IOException) return "响应中断";

        return null;
    }

    /// <summary>空回复判定：没有工具调用、正文与思考都是空白。</summary>
    public static bool IsEmptyReply(AssistantReply reply) =>
        reply.ToolCalls.Count == 0 &&
        string.IsNullOrWhiteSpace(reply.Content) &&
        string.IsNullOrWhiteSpace(reply.ReasoningContent);

    /// <summary>
    /// 退避时长：800ms × 2^(n-1)，封顶 8s，+ 0~200ms 抖动。
    /// 抖动不引随机库：取当前时间亚秒纳秒数，足够打散同时重试的请求。
    /// </summary>
    public static TimeSpan Backoff(int attempt)
    {
        var exp = Math.Min(Math.Max(attempt - 1, 0), 4);
        var baseMs = BaseBackoffMs << exp;   // 800, 1600, 3200, 6400, 12800
        var capped = Math.Min(baseMs, MaxBackoffMs);
        var jitter = (int)(DateTime.UtcNow.Ticks / 10 % 200);   // 0~199ms
        return TimeSpan.FromMilliseconds(capped + jitter);
    }
}
