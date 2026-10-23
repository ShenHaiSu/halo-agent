using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Host.Desktop.LlmGateway;

/// <summary>P0-1 观测层：包住 SendAsync，记录 requestBytes/status/headers/errno，原样抛出。</summary>
public sealed class ProviderResponseCapture
{
    public sealed record CaptureResult(
        int RequestBytes,
        HttpStatusCode? Status,
        IReadOnlyDictionary<string, string> ResponseHeaders,
        string? Errno
    );

    public static string? ClassifyErrno(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "canceled_or_timeout";
        if (ex is TaskCanceledException)
            return "task_canceled";
        if (ex is TimeoutException)
            return "timeout";
        if (ex is HttpRequestException hre && hre.InnerException is SocketException se)
            return $"socket_{(int)se.SocketErrorCode}_{se.SocketErrorCode}";
        if (ex is HttpRequestException)
            return "http_request_error";
        if (ex is IOException)
            return "io_error";
        return ex.GetType().Name;
    }

    public enum ProviderErrorKind
    {
        RateLimited,
        Terminal,
        Transient
    }

    /// <summary>
    /// 429→RateLimited；鉴权/上下文超限(401/403/400明确提及context/token)→Terminal；其余5xx/传输异常→Transient。
    /// Terminal 永不重试。
    /// </summary>
    public static ProviderErrorKind ClassifyProviderError(
        int? status,
        Exception? ex,
        string? errorBody = null
    )
    {
        if (status == 429)
            return ProviderErrorKind.RateLimited;
        if (status == 401 || status == 403)
            return ProviderErrorKind.Terminal;
        if (
            status == 400
            && errorBody != null
            && (
                errorBody.Contains("context", StringComparison.OrdinalIgnoreCase)
                || errorBody.Contains("token", StringComparison.OrdinalIgnoreCase)
                || errorBody.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
            )
        )
            return ProviderErrorKind.Terminal;
        if (status is >= 500 and <= 599)
            return ProviderErrorKind.Transient;
        if (status == 408 || status == 409)
            return ProviderErrorKind.Transient;
        if (ex != null)
            return ProviderErrorKind.Transient;
        return ProviderErrorKind.Transient;
    }

    public static async Task<(
        HttpResponseMessage Response,
        CaptureResult Capture
    )> SendAndCaptureAsync(
        HttpClient client,
        HttpRequestMessage request,
        string payload,
        CancellationToken ct
    )
    {
        int requestBytes = Encoding.UTF8.GetByteCount(payload);
        try
        {
            var resp = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in resp.Headers)
                headers[kv.Key.ToLowerInvariant()] = string.Join(",", kv.Value);
            if (resp.Content?.Headers != null)
                foreach (var kv in resp.Content.Headers)
                    headers[kv.Key.ToLowerInvariant()] = string.Join(",", kv.Value);
            return (resp, new CaptureResult(requestBytes, resp.StatusCode, headers, null));
        }
        catch (Exception ex)
        {
            string? errno = ClassifyErrno(ex);
            throw;
        }
    }
}
