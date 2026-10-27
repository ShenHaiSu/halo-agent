using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HaloAgent.Engine.Server.Tests;

/// <summary>
/// stdio 通道的端到端往返测试。
/// 这个测试**证明了"后端可独立测试"**：不需要 CefSharp、不需要窗口、
/// 不需要管道、不需要网络。整个引擎被当成一个命令行程序驱动。
/// 这也是 P0 的核心验收物之一。
/// </summary>
public class StdioRoundtripTests
{
    private static string FindEngineExe()
    {
        // 从测试程序集目录往上找。注意输出目录随构建方式而变：
        //   - dotnet test 默认（AnyCPU）：bin\Release\net6.0-windows\
        //   - sln 按平台构建：bin\x86\Release\net6.0-windows\（或 x64）
        //   - 单项目 -r 构建：bin\Release\net6.0-windows\win-x86\
        // 顺序重要：优先与测试同一次构建产出的引擎（AnyCPU，可在本机运行）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string[] tails = new[]
        {
            Path.Combine("bin", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
            Path.Combine("bin", "Release", "net6.0-windows", "win-x86", "HaloAgent.Engine.exe"),
            Path.Combine("bin", "x86", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
            Path.Combine("bin", "x64", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
        };
        while (dir is not null)
        {
            foreach (var tail in tails)
            {
                var cand = Path.Combine(dir.FullName, "src", "Engine.Server", tail);
                if (File.Exists(cand)) return cand;
            }

            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 HaloAgent.Engine.exe，请先构建 Engine.Server（Release）");
    }
    [Fact]
    public async Task Ping_returns_pong_and_engine_version()
    {
        var psi = new ProcessStartInfo(FindEngineExe())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--stdio");

        using var proc = Process.Start(psi)!;
        try
        {
            var sw = Stopwatch.StartNew();

            await proc.StandardInput.WriteLineAsync("{\"t\":\"req\",\"id\":1,\"m\":\"system.ping\"}");
            await proc.StandardInput.FlushAsync();

            var line = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            var root = doc.RootElement;

            Assert.Equal("res", root.GetProperty("t").GetString());
            Assert.Equal(1, root.GetProperty("id").GetInt64());
            Assert.True(root.GetProperty("ok").GetBoolean());

            var r = root.GetProperty("r");
            Assert.True(r.GetProperty("pong").GetBoolean());
            Assert.False(string.IsNullOrEmpty(r.GetProperty("version").GetString()));
            Assert.True(r.GetProperty("protocol").GetInt32() >= 1);

            Assert.True(sw.ElapsedMilliseconds < 5000, $"往返耗时 {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public async Task Unknown_method_returns_error_frame()
    {
        var psi = new ProcessStartInfo(FindEngineExe())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--stdio");

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.StandardInput.WriteLineAsync("{\"t\":\"req\",\"id\":9,\"m\":\"nope.nothing\"}");
            await proc.StandardInput.FlushAsync();

            var line = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            using var doc = JsonDocument.Parse(line!);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("method.notFound",
                doc.RootElement.GetProperty("e").GetProperty("code").GetString());
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public async Task Stdio_mode_does_not_write_logs_to_stdout()
    {
        // 这条测试守护"日志不能污染帧流"这个坑
        var psi = new ProcessStartInfo(FindEngineExe())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--stdio");
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add("trace");   // 最啰嗦的级别

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.StandardInput.WriteLineAsync("{\"t\":\"req\",\"id\":1,\"m\":\"system.ping\"}");
            await proc.StandardInput.FlushAsync();

            var line = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            // 第一行必须是合法 JSON 帧，不能是 "HH:mm:ss.fff [INF] ..."
            Assert.StartsWith("{", line);
            using var doc = JsonDocument.Parse(line!);   // 解析失败就说明被污染了
            Assert.Equal("res", doc.RootElement.GetProperty("t").GetString());
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }
}
