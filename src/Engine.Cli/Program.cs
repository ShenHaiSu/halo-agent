using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Cli;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Channels;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Server;
using HaloAgent.Engine.Server.Routing;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging;

// 顶层语句。无头入口：
//   -p <任务>        跑一轮完整 agent 循环后退出（退出码 0/1/2/3/4）
//   --stdio          复用 Engine.Server 的帧服务（与 HaloAgent.Engine.exe --stdio 同行为）
//   --diagnostics    打印环境摘要后退出

// ============ TLS 1.2（Win7 必需，必须是第一件事）============
System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

CliOptions opts;
try
{
    opts = CliOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("参数错误: " + ex.Message);
    return 2;
}

if (opts.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

// --data-dir 优先：对齐 Paths（SessionStore / Config / Undo 都从它取值）
if (!string.IsNullOrWhiteSpace(opts.DataDir))
    Environment.SetEnvironmentVariable("HALO_DATA_DIR", opts.DataDir.Trim());

if (opts.Diagnostics)
{
    var diagOpts = new EngineStartupOptions() with
    {
        Diagnostics = true,
        DataDir = string.IsNullOrWhiteSpace(opts.DataDir)
            ? EngineStartupOptions.DefaultDataDir()
            : opts.DataDir.Trim(),
    };
    Console.WriteLine(EngineLogging.DescribeEnvironment(diagOpts));
    return 0;
}

// ============ 帧服务模式（--stdio / --pipe-name）：P1 行为，原样保留 ============
if (opts.Stdio || !string.IsNullOrWhiteSpace(opts.PipeName))
{
    var serverOpts = new EngineStartupOptions() with
    {
        Mode = !string.IsNullOrWhiteSpace(opts.PipeName) ? EngineRunMode.Pipe : EngineRunMode.Stdio,
        PipeName = opts.PipeName,
        DataDir = string.IsNullOrWhiteSpace(opts.DataDir)
            ? EngineStartupOptions.DefaultDataDir()
            : opts.DataDir.Trim(),
        LogLevel = opts.LogLevel,
    };
    return await RunFrameServiceAsync(serverOpts);
}

if (string.IsNullOrWhiteSpace(opts.Prompt))
{
    Console.Error.WriteLine("错误: 缺少 -p <任务>");
    return 2;
}

// ============ 无头任务模式 ============
Paths.EnsureDataDirs();

ConfigModel cfg;
try
{
    cfg = ConfigStore.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine("读取配置失败: " + ex.Message);
    return 3;
}

ResolvedActiveModel resolved;
try
{
    resolved = ConfigStore.Resolve(cfg);
}
catch (Exception ex)
{
    Console.Error.WriteLine("配置未就绪: " + ex.Message);
    return 3;
}

if (string.Equals(resolved.Protocol, "responses", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("配置未就绪: Responses 协议尚未支持（当前仅 chat 可用）。");
    return 3;
}

if (!PermissionModeExtensions.TryParseWire(opts.Permission, out var mode))
{
    Console.Error.WriteLine(
        "未知权限模式: " + opts.Permission + "（可选: ask / acceptEdits / bypassPermissions / yolo）");
    return 2;
}

var cwd = opts.Cwd ?? Environment.CurrentDirectory;
if (!Directory.Exists(cwd))
{
    Console.Error.WriteLine("工作目录不存在: " + cwd);
    return 2;
}

using var llm = new OpenAiCompatibleClient(new LlmConfig
{
    BaseUrl = resolved.BaseUrl,
    Model = resolved.ModelId,
    ApiKey = resolved.ApiKey,
    ProxyUrl = resolved.ProxyUrl,
    Headers = resolved.Headers,
    Protocol = LlmProtocol.Chat,
    MaxTokens = resolved.MaxTokens,
    AcceptsImages = resolved.AcceptsImages,
    IncludeReasoningHistory = resolved.IncludeReasoningHistory,
});


var tools = ToolRegistry.CreateBuiltin();
var store = new SessionStore();
store.StartNew();

// 无头模式没有 UI：用一个空事件总线接收事件（只为了满足依赖）。
// Start() 必须调：否则队满会反压阻塞 agent 循环。
var nullBus = NullEventBus.Create();
var bus = nullBus.Adapter;
bus.Start();

await using var session = new AgentSession(
    llm, tools, store, bus,
    Paths.DataDir, cwd, mode, resolved.Persona,
    maxTurns: opts.MaxTurns ?? resolved.MaxTurns,
    retry: opts.Retry ?? resolved.Retry,
    askInteractive: false);
session.ReconfigureActive(resolved);


using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    session.CancelTurn();
};

var sw = Stopwatch.StartNew();
TurnResult result;
try
{
    result = await session.RunTurnAsync(opts.Prompt!, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("⏹ 已中断");
    return 4;
}
catch (Exception ex)
{
    Console.Error.WriteLine("任务失败: " + ex.Message);
    return 1;
}
sw.Stop();

if (opts.Json)
{
    var payload = new
    {
        ok = !result.Truncated,
        text = result.Text,
        toolCalls = result.ToolCalls,
        rounds = session.BuildSnapshot().Agent.Round.Used,
        truncated = result.Truncated,
        usage = new { input = result.InputTokens, output = result.OutputTokens },
        durationMs = sw.ElapsedMilliseconds,
        sessionId = store.SessionId,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
}
else
{
    Console.WriteLine(result.Text);
    Console.WriteLine();
    Console.WriteLine("---");
    Console.WriteLine(
        "用时 " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s · 工具调用 " + result.ToolCalls + " 次 · " +
        "轮次 " + session.BuildSnapshot().Agent.Round.Used + " · " +
        "token in " + result.InputTokens + " out " + result.OutputTokens);
    Console.WriteLine("会话 " + store.SessionId);
}

return result.Truncated ? 1 : 0;

static async Task<int> RunFrameServiceAsync(EngineStartupOptions serverOpts)
{
    // ⚠️ stdio 模式下 stdout 归帧独占，绝不能写日志到 stdout（否则宿主读到的第一行是日志）。
    bool canWriteConsole = serverOpts.Mode != EngineRunMode.Stdio;
    using var loggerFactory = EngineLogging.Create(serverOpts.LogLevel, serverOpts.DataDir, "cli", canWriteConsole);
    var log = loggerFactory.CreateLogger("cli");

    using var cts = new CancellationTokenSource();
    await using IFrameChannel channel = serverOpts.Mode switch
    {
        EngineRunMode.Stdio => new StdioChannel(),
        EngineRunMode.Pipe => await NamedPipeChannel.AcceptAsync(serverOpts.PipeName ?? throw new InvalidOperationException("缺少 --pipe-name"), log, cts.Token),
        _ => throw new ArgumentOutOfRangeException(),
    };
    var router = new MethodRouter();
    var state = new EngineState(serverOpts.DataDir, router);
    var stubs = new StubMethods(state);
    StubMethods.RegisterAll(router, stubs);
    await using var bus = new EventBus(channel, loggerFactory.CreateLogger("bus"));
    var server = new FrameServer(channel, state, bus, loggerFactory.CreateLogger<FrameServer>(), cts.Token);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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
        log.LogError(ex, "Cli 异常退出");
        return 1;
    }

    return 0;
}
