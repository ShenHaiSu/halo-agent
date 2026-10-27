using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Server;
using HaloAgent.Engine.Server.Channels;
using Microsoft.Extensions.Logging;

// 顶层语句。无头入口（P0）：
//   --stdio        复用 Engine.Server 的帧服务（与 HaloAgent.Engine.exe --stdio 同行为）
//   -p <method>    单发一次请求（默认 system.ping），打印应答后退出
//   --diagnostics  打印环境摘要后退出

System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var argsList = args.ToList();

if (argsList.Contains("-h") || argsList.Contains("--help"))
{
    Console.WriteLine(CliHelp.Text);
    return 0;
}

int pIndex = argsList.IndexOf("-p");

var opts = EngineStartupOptions.Parse(argsList.Where(a => a != "-p" && !IsPromptValue(argsList, a)).ToArray());
// stdio 模式与 -p 单发模式下 stdout 归帧/应答独占，日志只写文件
bool canWriteConsole = opts.Mode != EngineRunMode.Stdio && pIndex < 0;
using var loggerFactory = EngineLogging.Create(opts.LogLevel, opts.DataDir, "cli", canWriteConsole);
var log = loggerFactory.CreateLogger("cli");

if (opts.Diagnostics)
{
    Console.WriteLine(EngineLogging.DescribeEnvironment(opts));
    return 0;
}

if (pIndex >= 0)
{
    string method = (pIndex + 1 < argsList.Count) ? argsList[pIndex + 1] : "system.ping";
    return await OneShotPingAsync(method, opts);
}

// 默认：与引擎进程相同的 stdio 帧服务（复用通道层与 FrameServer）
await using IFrameChannel channel = opts.Mode switch
{
    EngineRunMode.Stdio => new StdioChannel(),
    EngineRunMode.Pipe => throw new NotSupportedException("Named Pipe 通道在 P1 实现"),
    _ => throw new ArgumentOutOfRangeException(),
};

var server = new FrameServer(channel, loggerFactory.CreateLogger<FrameServer>());
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    log.LogInformation("已优雅退出");
}
catch (Exception ex)
{
    log.LogError(ex, "Cli 异常退出");
    return 1;
}

return 0;

static bool IsPromptValue(List<string> all, string a)
{
    int i = all.IndexOf("-p");
    return i >= 0 && i + 1 < all.Count && all[i + 1] == a;
}

/// <summary>
/// 单发模式：拉起 HaloAgent.Engine.exe --stdio，发一次 system.ping（或指定方法），打印应答后退出。
/// 证明"后端可在无 UI 进程的情况下被驱动"（A-07 的另一条手工路径）。
/// </summary>
static async Task<int> OneShotPingAsync(string method, EngineStartupOptions opts)
{
    string? dir = AppContext.BaseDirectory;
    string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "HaloAgent.Engine.exe" : "HaloAgent.Engine");
    if (!File.Exists(exe))
    {
        // 开发态：从源码布局推断（输出目录随构建方式而变，逐个试）
        string[] probes = new[]
        {
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                "Engine.Server", "bin", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                "Engine.Server", "bin", "x86", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                "Engine.Server", "bin", "x64", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
        };
        foreach (var probe in probes)
        {
            if (File.Exists(probe)) { exe = probe; break; }
        }
    }
    if (!File.Exists(exe))
    {
        Console.Error.WriteLine("找不到 HaloAgent.Engine.exe，请先构建 Engine.Server（Release）。");
        return 2;
    }

    var psi = new ProcessStartInfo(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        // 必须无 BOM（见 EngineClient 同处注释）：Encoding.UTF8 会写出 EF BB BF 前导。
        StandardInputEncoding = new UTF8Encoding(false),
        WorkingDirectory = dir,
    };
    psi.ArgumentList.Add("--stdio");
    psi.ArgumentList.Add("--data-dir");
    psi.ArgumentList.Add(opts.DataDir);

    using var proc = Process.Start(psi);
    if (proc is null)
    {
        Console.Error.WriteLine("无法启动引擎进程。");
        return 1;
    }

    try
    {
        long id = 1;
        await proc.StandardInput.WriteLineAsync($"{{\"t\":\"req\",\"id\":{id},\"m\":{JsonSerializer.Serialize(method)}}}");
        await proc.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line = await proc.StandardOutput.ReadLineAsync().WaitAsync(cts.Token);
        if (string.IsNullOrEmpty(line))
        {
            // 无应答时把引擎的 stderr 与退出码吐出来，否则只能看到"(无应答)"，无法定位
            string err = "";
            try { err = await proc.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            int code = -1;
            try { code = proc.HasExited ? proc.ExitCode : -1; } catch { }
            Console.Error.WriteLine($"引擎无应答（exe={exe}，已退出={proc.HasExited}，退出码={code}）：{err.Trim()}");
            Console.WriteLine("(无应答)");
            return 1;
        }
        Console.WriteLine(line);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"单发请求失败: {ex.Message}");
        return 1;
    }
    finally
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.StandardInput.Close();
                if (!proc.WaitForExit(2000)) proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* 退出清理不抛异常 */ }
    }
}

static class CliHelp
{
    public const string Text =
        "HaloAgent 无头入口（Cli）\n" +
        "\n" +
        "用法: HaloAgent.Cli.exe [选项]\n" +
        "\n" +
        "  --stdio                走 stdin/stdout 收发帧（与引擎进程同行为）\n" +
        "  -p [method]            单发一次请求并打印应答（默认 system.ping），后退出\n" +
        "  --data-dir <路径>      数据目录（默认 %USERPROFILE%\\.haloagent）\n" +
        "  --log <级别>           trace|debug|info|warn|error（默认 info）\n" +
        "  --diagnostics          打印环境摘要后退出\n" +
        "  -h, --help             显示本帮助\n" +
        "\n" +
        "示例:\n" +
        "  HaloAgent.Cli.exe -p system.ping\n" +
        "  echo {\"t\":\"req\",\"id\":1,\"m\":\"system.ping\"} | HaloAgent.Cli.exe --stdio\n";
}
