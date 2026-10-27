using HaloAgent.Desktop.Browser;
using Xunit;

namespace HaloAgent.Host.Tests.Browser;

/// <summary>
/// MIME 回归锁：MimeType 必须是裸类型（text/html），绝不能带 "; charset=…"。
/// 带参数的 MimeType 会让 CEF 认不出类型，按 text/plain 处理——页面直接显示
/// HTML 源码，JS/CSS 全灭（2026-10-27 真机黑屏/源码屏事故）。
/// 字符集一律走 ResourceHandler.Charset。
/// </summary>
public sealed class SpaMimeTests
{
    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("page.htm", "text/html")]
    [InlineData("app.js", "text/javascript")]
    [InlineData("app.mjs", "text/javascript")]
    [InlineData("site.css", "text/css")]
    [InlineData("data.json", "application/json")]
    [InlineData("app.js.map", "application/json")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("icon.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("anim.webp", "image/webp")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("readme.txt", "text/plain")]
    public void GetMimeType_ReturnsBareType_WithoutParameters(string file, string expected)
    {
        var mime = SpaSchemeHandlerFactory.GetMimeType(file);
        Assert.Equal(expected, mime);
        Assert.DoesNotContain(";", mime);
        Assert.DoesNotContain("charset", mime);
    }
}
