using System.Net;
using CefSharp;

namespace HaloAgent.Desktop.Browser;

/// <summary>
/// 带 SPA 兜底的 app:// 资源 handler。
/// 规则：无扩展名 → 回退 index.html(200)；有扩展名缺失 → 如实 404；
/// 越界 → 403；未知 host → 404。文本 MIME 一律带 charset=utf-8。
/// 注意（CefSharp 109 真实 API）：ResourceHandler.FromString 无 statusCode 参数，
/// 错误页用 ForErrorMessage(message, code)；Headers 只能在具体类上设。
/// </summary>
public sealed class SpaSchemeHandlerFactory : ISchemeHandlerFactory
{
    private readonly string _rootFolder;
    private readonly string _defaultPage;

    public SpaSchemeHandlerFactory(string rootFolder, string defaultPage = "index.html")
    {
        _rootFolder = Path.GetFullPath(rootFolder);
        _defaultPage = defaultPage;
    }

    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        try
        {
            return CreateCore(request);
        }
        catch (Exception ex)
        {
            // handler 里抛异常会导致整页白屏且无提示，必须兜住。
            return ResourceHandler.ForErrorMessage(
                $"资源加载失败：{ex.Message}", HttpStatusCode.InternalServerError);
        }
    }

    private IResourceHandler CreateCore(IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return NotFound("URL 无法解析");
        if (!string.Equals(uri.Host, "local", StringComparison.OrdinalIgnoreCase))
            return NotFound("未知的 host");

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (string.IsNullOrEmpty(path) || path == "/")
            path = "/" + _defaultPage;

        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_rootFolder, relative));
        }
        catch (Exception)
        {
            return NotFound("非法路径");
        }

        // 目录穿越防护：必须 GetFullPath 后比较前缀。
        if (!candidate.StartsWith(_rootFolder, StringComparison.OrdinalIgnoreCase))
            return Forbidden("路径越界");

        if (File.Exists(candidate))
            return ServeFile(candidate);

        // SPA 兜底：只有无扩展名的导航才回退；带扩展名的缺失如实 404
        //（否则 .js 的 404 变成 html，报 MIME 错误）。
        if (Path.GetExtension(path).Length == 0)
        {
            var index = Path.Combine(_rootFolder, _defaultPage);
            if (File.Exists(index))
                return ServeFile(index);
        }

        return NotFound($"资源不存在：{path}");
    }

    private static IResourceHandler ServeFile(string fullPath)
    {
        // 注意（CEF 真实行为）：MimeType 必须是裸类型（如 text/html）。
        // 若把 "text/html; charset=utf-8" 整个塞进 MimeType，CEF 认不出类型，
        // 会按 text/plain 处理——页面直接显示 HTML 源码，JS/CSS 也全灭。
        // 字符集走 Charset 属性（CEF 会拼成 Content-Type: text/html;charset=utf-8）。
        var handler = ResourceHandler.FromFilePath(fullPath, GetMimeType(fullPath));
        if (handler is ResourceHandler rh)
        {
            if (IsTextType(fullPath)) rh.Charset = "utf-8";
            var isHtml = fullPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
            rh.Headers.Add("Cache-Control", isHtml
                ? "no-cache, no-store, must-revalidate"
                : "public, max-age=31536000, immutable");
        }
        return handler;
    }

    private static bool IsTextType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" or ".js" or ".mjs" or ".css" or ".json" or ".map" or ".txt" or ".svg" => true,
        _ => false,
    };

    private static IResourceHandler NotFound(string hint) =>
        ResourceHandler.ForErrorMessage($"404 — {hint}", HttpStatusCode.NotFound);

    private static IResourceHandler Forbidden(string hint) =>
        ResourceHandler.ForErrorMessage($"403 — {hint}", HttpStatusCode.Forbidden);

    public static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".wasm" => "application/wasm",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}
