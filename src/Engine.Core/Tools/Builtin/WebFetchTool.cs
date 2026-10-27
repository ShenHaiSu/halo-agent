using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Util;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// web_fetch：抓网页转纯文本。
///
/// ============ 注意 ============
/// 这是<b>唯一</b>会让模型触发外部网络请求的工具。
/// 因此：
///   - 只允许 http/https
///   - 走与模型请求同一个代理出口
///   - 输出截断到 max_chars（缺省 8000）
///   - UA 明确标识自己（不伪装浏览器）
/// </summary>
public sealed class WebFetchTool : ITool
{
    private const int DefaultMaxChars = 8000;

    public string Name => "web_fetch";
    public string Description =>
        "抓取一个网页(http/https)并转为纯文本。适合查资料、读文档、看新闻。" +
        "网络访问受用户网络环境影响,失败时如实说明";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""url"":       { ""type"": ""string"", ""description"": ""要抓取的完整 URL"" },
        ""max_chars"": { ""type"": ""integer"", ""description"": ""最多返回字符数,缺省 8000"" }
      },
      ""required"": [""url""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var url = args.GetStringOrNull("url");
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failure("url 不能为空");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failure("url 必须以 http:// 或 https:// 开头");

        var maxChars = (int)Math.Min(args.GetLongOrNull("max_chars") ?? DefaultMaxChars, 200_000);

        HttpClient client;
        try
        {
            client = HaloAgent.Engine.Core.Net.HttpClientFactory.Create(ctx.Host.ProxyUrl, timeoutSeconds: 30);
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"HTTP 客户端初始化失败: {ex.Message}");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("HaloAgent/0.1 (desktop AI assistant)");

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
                return ToolResult.Failure($"抓取 {url} 失败: HTTP {status}");

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            // 简单编码判断：优先 UTF-8，失败则按 GB18030（中文站常见）
            string html;
            try { html = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
            catch
            {
                try { html = Encoding.GetEncoding("GB18030").GetString(bytes); }
                catch { html = Encoding.Latin1.GetString(bytes); }
            }

            var plain = HtmlToText(html);
            var outText = TextUtil.TruncateChars(plain, maxChars, "\n…(已截断)");

            return ToolResult.Success(
                $"抓取 {url}(HTTP {status},{plain.Length} 字符):\n\n{outText}");
        }
        catch (TaskCanceledException) { return ToolResult.Failure($"抓取 {url} 超时（30s）"); }
        catch (HttpRequestException ex) { return ToolResult.Failure($"抓取 {url} 失败: {ex.Message}"); }
        finally { client.Dispose(); }
    }

    /// <summary>
    /// 简易 HTML → 文本。
    /// 不是完整解析器（不引 HTML 库，减依赖），但覆盖 95% 场景：
    /// 去 script/style/head/noscript/svg 区块 → 去标签 → 解实体 → 折叠空行。
    /// </summary>
    internal static string HtmlToText(string html)
    {
        var text = html;
        foreach (var tag in new[] { "script", "style", "head", "noscript", "svg" })
            text = StripBlock(text, tag);

        // 去标签
        var sb = new StringBuilder(text.Length);
        bool inTag = false;
        foreach (var c in text)
        {
            if (inTag)
            {
                if (c == '>') inTag = false;
                continue;
            }
            if (c == '<') { inTag = true; continue; }
            sb.Append(c);
        }

        var outText = sb.ToString()
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&#x27;", "'")
            .Replace("&ndash;", "–")
            .Replace("&mdash;", "—")
            .Replace("&hellip;", "…");

        // 折叠多余空行（连续空行最多保留 1 个）
        var lines = new List<string>();
        int blank = 0;
        foreach (var line in outText.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                blank++;
                if (blank <= 1) lines.Add("");
            }
            else
            {
                blank = 0;
                lines.Add(t);
            }
        }
        return string.Join("\n", lines).Trim();
    }

    private static string StripBlock(string html, string tag)
    {
        var open = "<" + tag;
        var close = "</" + tag + ">";
        var lower = html.ToLowerInvariant();
        var sb = new StringBuilder(html.Length);
        int i = 0;

        while (i < html.Length)
        {
            var idx = lower.IndexOf(open, i, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }
            sb.Append(html, i, idx - i);

            var tagEnd = lower.IndexOf('>', idx);
            if (tagEnd < 0) break;

            // 自闭合标签（<svg/>）不需要找闭合
            if (html[tagEnd - 1] == '/')
            {
                i = tagEnd + 1;
                continue;
            }

            var closeIdx = lower.IndexOf(close, tagEnd, StringComparison.Ordinal);
            i = closeIdx < 0 ? html.Length : closeIdx + close.Length;
        }
        return sb.ToString();
    }
}
