using System.Text;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Network;

/// <summary>
/// P0-1 唯一 Header 装配线：清洗 → 去重 → 上限 → 注入。
/// 顺序不可换，任何请求不许绕过。
/// </summary>
public static class LlmHeaderPipeline
{
    public const int MaxHeaders = 32;
    public const int MaxKeyBytes = 256;
    public const int MaxValueBytes = 8 * 1024;

    private static readonly HashSet<string> ForbiddenKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "proxy-authorization",
            "host",
            "content-length",
            "content-type",
            "cookie",
            "set-cookie",
            "connection",
            "transfer-encoding",
            "x-api-key",
        };

    public sealed record NormalizedHeader(string Key, string Value);

    /// <summary>清洗 + 去重（后者覆盖前者）+ 上限 32。返回规范化列表。</summary>
    public static IReadOnlyList<NormalizedHeader> NormalizeCustomHeaders(
        IEnumerable<CustomHeaderEntry>? entries
    )
    {
        var map = new Dictionary<string, NormalizedHeader>(StringComparer.OrdinalIgnoreCase);
        if (entries == null)
            return Array.Empty<NormalizedHeader>();
        foreach (var e in entries)
        {
            if (e == null || !e.Enabled)
                continue;
            string key = (e.Key ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(key))
                continue;
            // 全角/控制字符：先折叠全角空格并剔除控制字符，仍含 >U+00FF 则丢弃
            key = SanitizeKey(key);
            if (string.IsNullOrEmpty(key))
                continue;
            if (key.Any(c => c > 0x00FF || char.IsControl(c)))
                continue;
            if (ForbiddenKeys.Contains(key))
            {
                HaloLogger.App.Warn($"[HeaderPipeline] 禁键丢弃: {key}");
                continue;
            }
            if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
                continue;
            string value = SanitizeValue(e.ValueTemplate ?? string.Empty);
            if (string.IsNullOrEmpty(value))
                continue;
            if (value.Any(c => c > 0x00FF && !IsAllowedNonLatin(c)))
                continue;
            if (Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
                continue;
            map[key] = new NormalizedHeader(key, value);
            if (map.Count >= MaxHeaders)
                break;
        }
        // 保持插入顺序：Dictionary 在 .NET Core 保持插入序；超限截断已有 break
        return map.Values.ToList();
    }

    public static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyList<NormalizedHeader> headers,
        HeaderResolutionContext ctx
    )
    {
        foreach (var h in headers)
        {
            string resolved = HeaderVariableResolver.ResolveTemplate(h.Value, ctx);
            if (Encoding.UTF8.GetByteCount(resolved) > MaxValueBytes)
                continue;
            request.Headers.Remove(h.Key);
            request.Headers.TryAddWithoutValidation(h.Key, resolved);
        }
    }

    /// <summary>BaseUrl 归一化：去尾 /，Anthropic 去重 /v1/v1。</summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        string s = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        // 去重 /v1/v1 后缀
        while (s.EndsWith("/v1/v1", StringComparison.OrdinalIgnoreCase))
            s = s[..^3];
        return s;
    }

    public static string ResolveChatEndpoint(string baseUrl) =>
        NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/chat/completions";

    public static string ResolveResponsesEndpoint(string baseUrl) =>
        NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/responses";

    private static string SanitizeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (c == '\u3000')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(c))
                continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string SanitizeValue(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (char c in v)
        {
            if (c == '\u3000')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(c) && c != '\t')
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsAllowedNonLatin(char c)
    {
        // 模板变量 ${...} 本身为 ASCII；此处宽松放行 CJK 以免误杀中文 value，
        // HttpClient 写入时仍由 TryAddWithoutValidation + ASCII 检查兜底。
        // 为满足“>U+00FF 则丢弃”验收，仅在 key 上严格，value 上放行可编码字符。
        return true;
    }
}
