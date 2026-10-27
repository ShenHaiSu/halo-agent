using HaloAgent.Engine.Server;
using HaloAgent.Engine.Server.Channels;
using HaloAgent.Engine.Abstractions;
using Microsoft.Extensions.Logging;

// 顶层语句（C# 9+）。引擎进程的一切都在这里起。

// ---- 铁律：TLS 1.2（Win7 默认未启用，C# HttpClient 会握手失败） ----
// 必须是 Main 的第一件事。Chromium 自带 BoringSSL 不受影响，
// 但引擎侧的所有 HTTP（模型请求、网页抓取、更新检查）都依赖它。
System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var opts = EngineStartupOptions.Parse(args);

if (opts.ShowHelp)
{
    Console.WriteLine(EngineStartupOptions.HelpText);
    return opts.ExitCodeSuccess;
}

// ⚠️ stdio 模式下 stdout 归帧独占，绝不能写日志到 stdout（否则宿主读到的第一行是日志）。
bool canWriteConsole = opts.Mode != EngineRunMode.Stdio;
using var loggerFactory = EngineLogging.Create(opts.LogLevel, opts.DataDir, "engine", canWriteConsole);
var log = loggerFactory.CreateLogger("engine");

log.LogInformation(
    "引擎启动 v{Version} pid={Pid} mode={Mode} data={Data} os={Os}",
    HaloVersion.Engine, Environment.ProcessId, opts.Mode, opts.DataDir, Environment.OSVersion);

if (opts.Diagnostics)
{
    Console.WriteLine(EngineLogging.DescribeEnvironment(opts));
    return opts.ExitCodeSuccess;
}

await using IFrameChannel channel = opts.Mode switch
{
    EngineRunMode.Stdio => new StdioChannel(),
    // P1 补齐
    EngineRunMode.Pipe => throw new NotSupportedException("Named Pipe 通道在 P1 实现"),
    _ => throw new ArgumentOutOfRangeException(),
};

var server = new FrameServer(channel, loggerFactory.CreateLogger<FrameServer>());
using var cts = new CancellationTokenSource();

// Ctrl+C / 关闭 → 优雅退出
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    log.LogInformation("收到中断信号，准备退出");
    cts.Cancel();
};

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
    log.LogError(ex, "引擎异常退出");
    return 1;
}

return 0;
