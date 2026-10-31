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

     // ============================================================
     //  桥契约（dist 骨架屏根因修复）：DTO/事件批一律序列化为 JSON 字符串过桥，
     //  且键必须为 camelCase（前端按 camelCase 消费，无归一化层）。
     //  若这里失败，说明有人又把 DTO/POCO 直接返回给了 CefSharp。
     // ============================================================

     [Fact]
     public void Bridge_poll_result_serializes_to_camelCase_json()
     {
         var batch = new PollResult { Events = Array.Empty<object>(), NextSeq = 7 };
         using var doc = JsonDocument.Parse(JsonSerializer.Serialize(batch, BridgeJson.Options));
         Assert.True(doc.RootElement.TryGetProperty("events", out _));
         Assert.True(doc.RootElement.TryGetProperty("nextSeq", out _));
         Assert.False(doc.RootElement.TryGetProperty("Events", out _));
         Assert.False(doc.RootElement.TryGetProperty("NextSeq", out _));
     }

     [Fact]
     public void Bridge_window_state_serializes_to_camelCase_json()
     {
         var ws = new WindowStateDto
         {
             Maximized = true, Minimized = false, Width = 1280, Height = 820, Dpi = 96, TitleBarHeight = 40,
         };
         using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ws, BridgeJson.Options));
         Assert.True(doc.RootElement.TryGetProperty("maximized", out _));
         Assert.True(doc.RootElement.TryGetProperty("titleBarHeight", out _));
         Assert.False(doc.RootElement.TryGetProperty("Maximized", out _));
     }

     [Fact]
     public void Bridge_host_info_serializes_to_camelCase_json()
     {
         var info = new HostSystemInfo
         {
             OsDescription = "os", DataDir = "d", Cwd = "c", GpuMode = "g",
             Screens = Array.Empty<ScreenDto>(),
         };
         using var doc = JsonDocument.Parse(JsonSerializer.Serialize(info, BridgeJson.Options));
         foreach (var key in new[] { "osDescription", "isWindows7", "dataDir", "cwd", "gpuMode", "screens" })
             Assert.True(doc.RootElement.TryGetProperty(key, out _), $"缺少键 {key}");
         Assert.False(doc.RootElement.TryGetProperty("OsDescription", out _));
     }

     [Fact]
     public void Bridge_engine_status_event_payload_is_camelCase_json()
     {
         // 与 HaloHostBridge.EmitEngineStatusAsync 的推送形状一致（前端按 state/pid 消费）。
         var payload = new EngineStatusDto { State = "ready", Pid = 123 };
         using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.Options));
         Assert.Equal("ready", doc.RootElement.GetProperty("state").GetString());
         Assert.Equal(123, doc.RootElement.GetProperty("pid").GetInt32());
     }

     [Fact]
     public void HaloHost_exposes_exactly_thirteen_bridge_methods()
     {
         // 与 scripts/p3-diag.html 的 HOST_METHODS / 前端 HaloHost 接口保持一致。
         // windowDragStart / windowResizeStart 是自绘标题栏 B 方案的显式拖拽/缩放入口（CEF 铺满客户区后
         // WM_NCHITTEST 到不了窗体，前端 mousedown 直调，宿主做 ReleaseCapture+SendMessage）。
         var expected = new[]
         {
             "WindowMinimize", "WindowDragStart", "WindowResizeStart", "WindowMaximizeOrRestore", "WindowClose",
             "WindowState", "SystemInfo", "OpenExternal", "ShowItemInFolder",
             "CopyText", "ReadClipboardImage", "ToggleDevTools", "RestartEngine",
         };
         var actual = typeof(HaloHostBridge)
             .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
             .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(HaloHostBridge))
             .Select(m => m.Name)
             .Where(n => !n.StartsWith("Emit", StringComparison.Ordinal))
             .OrderBy(n => n)
             .ToArray();
         Assert.Equal(expected.OrderBy(n => n).ToArray(), actual);
     }
 }
