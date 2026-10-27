using HaloAgent.Engine.Abstractions;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Server;
using HaloAgent.Engine.Core.Channels;
using HaloAgent.Engine.Server.Routing;
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

// Pipe 模式的管道名由宿主指定（随机后缀防猜测），引擎只负责监听，不自己取名。
if (opts.Mode == EngineRunMode.Pipe && string.IsNullOrWhiteSpace(opts.PipeName))
{
    Console.Error.WriteLine("缺少 --pipe-name（Pipe 模式下必填，由宿主生成并传入）");
    return 1;
}

 using var cts = new CancellationTokenSource();

 // Ctrl+C / 关闭 → 优雅退出
 Console.CancelKeyPress += (_, e) =>
 {
     e.Cancel = true;
     log.LogInformation("收到中断信号，准备退出");
     cts.Cancel();
 };

await using IFrameChannel channel = opts.Mode switch
{
    EngineRunMode.Stdio => new StdioChannel(),
    EngineRunMode.Pipe => await NamedPipeChannel.AcceptAsync(opts.PipeName!, log),
    _ => throw new ArgumentOutOfRangeException(nameof(opts.Mode), "未知运行模式"),
};

// P2 装配：真实 agent 循环 + 方法路由；失败时回落到 P1 stub（引擎仍可启动，
// 例如尚未配置模型时，设置页仍可用）。
AssembledEngine? asm = null;
MethodRouter router;
EngineState state;
EventBus bus;
try
{
    asm = EngineAssembly.BuildRouter(
        channel,
        opts.DataDir,
        loggerFactory.CreateLogger("bus"),
        loggerFactory.CreateLogger("methods"),
        cts.Token);
    router = asm.Router;
    state = asm.State;
    bus = asm.Bus;
}
catch (Exception ex)
{
    log.LogWarning(ex, "P2 装配失败，回落到 P1 stub");
    router = new MethodRouter();
    state = new EngineState(opts.DataDir, router);
    var stubs = new StubMethods(state);
    StubMethods.RegisterAll(router, stubs);
    bus = new EventBus(channel, loggerFactory.CreateLogger("bus"));
}

await using (asm)
{
    var server = new FrameServer(
        channel, state, bus, loggerFactory.CreateLogger<FrameServer>(), cts.Token);

    try
    {
        await server.RunAsync();
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
}

return 0;
