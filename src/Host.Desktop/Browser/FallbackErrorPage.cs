namespace HaloAgent.Desktop.Browser;

/// <summary>加载失败时的自绘错误页（data: URL），而不是让用户看 CEF 白屏。</summary>
public static class FallbackErrorPage
{
    public static string Build(string failedUrl, string errorCode, string errorText, string logFile, bool isDev)
    {
        var extra = isDev
            ? "<li>Vite dev server 是否已启动（<code>pnpm dev</code>）？</li>"
            : "<li>确认安装目录下存在 <code>wwwroot\\index.html</code></li><li>若刚重装，重新运行安装包</li>";
        var safe = System.Net.WebUtility.HtmlEncode(failedUrl);
        var log = System.Net.WebUtility.HtmlEncode(logFile);
        var html = $"<!doctype html><meta charset=\"utf-8\">"
            + $"<body style=\"font-family:'Microsoft YaHei','Microsoft YaHei UI','Segoe UI',sans-serif;background:#11151c;color:#e6ebf2;padding:40px\">"
            + $"<h2>页面加载失败</h2><p>错误码：{System.Net.WebUtility.HtmlEncode(errorCode)}（{System.Net.WebUtility.HtmlEncode(errorText)}）</p>"
            + $"<p>地址：{safe}</p><hr><p>排查：</p><ol>{extra}<li>日志：{log}</li></ol></body>";
        return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
    }
}
