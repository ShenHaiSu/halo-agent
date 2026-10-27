using System.Text.Json;
using HaloAgent.Desktop.Bridge;
using HaloAgent.Desktop.Browser;
using HaloAgent.Desktop.Diagnostics;
using Xunit;

namespace HaloAgent.Host.Tests.Bridge;

/// <summary>P3 桥接与资源契约：MIME 表 / Router-schema 一致 / PE 校验。</summary>
public sealed class HostContractTests
{
    [Fact]
    public void Mime_table_covers_required_extensions()
    {
        var required = new Dictionary<string, string>
        {
            [".html"] = "text/html",
            [".js"] = "text/javascript",
            [".mjs"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
            [".map"] = "application/json",
            [".wasm"] = "application/wasm",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
        };
        Assert.True(required.Count >= 18);
        foreach (var (ext, mime) in required)
            Assert.Contains(mime, SpaSchemeHandlerFactory.GetMimeType("x" + ext));
    }

    [Fact]
    public void Js_mime_is_exact()
    {
        // 裸类型：charset 走 ResourceHandler.Charset，绝不能拼进 MimeType
        //（否则 CEF 按 text/plain 处理，页面显示源码）。
        Assert.Equal("text/javascript", SpaSchemeHandlerFactory.GetMimeType("a.js"));
    }

    [Fact]
    public void Text_mimes_are_bare_charset_goes_to_handler()
    {
        foreach (var f in new[] { "a.html", "a.js", "a.mjs", "a.css", "a.json", "a.txt" })
        {
            var mime = SpaSchemeHandlerFactory.GetMimeType(f);
            Assert.DoesNotContain(";", mime);
            Assert.DoesNotContain("charset", mime);
        }
    }

    [Fact]
    public void Router_matches_schema_host_set()
    {
        var schemaHost = SchemaHostMethods();
        Assert.Equal(schemaHost.OrderBy(x => x).ToArray(), HostMethodRouter.Methods.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Router_has_exactly_four()
    {
        Assert.Equal(4, HostMethodRouter.Methods.Count);
        Assert.True(HostMethodRouter.IsHostMethod("fs.browse"));
        Assert.False(HostMethodRouter.IsHostMethod("session.new"));
    }

    [Fact]
    public void BinaryInspector_reads_current_process()
    {
        var exe = Environment.ProcessPath;
        Assert.False(string.IsNullOrEmpty(exe));
        var m = BinaryInspector.ReadMachine(exe!);
        Assert.Equal(BinaryInspector.CurrentProcess, m);
    }

    [Fact]
    public void BinaryInspector_rejects_garbage()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "hello");
            Assert.Equal(BinaryInspector.PeMachine.Unknown, BinaryInspector.ReadMachine(tmp));
            Assert.Equal(BinaryInspector.PeMachine.Unknown, BinaryInspector.ReadMachine(tmp + ".missing"));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static HashSet<string> SchemaHostMethods()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "contract", "agent.schema.json");
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                var set = new HashSet<string>();
                foreach (var m in doc.RootElement.GetProperty("methods").EnumerateObject())
                {
                    if (m.Value.TryGetProperty("implementedIn", out var v) &&
                        v.GetString() == "host")
                        set.Add(m.Name);
                }
                return set;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 contract/agent.schema.json");
    }
}
