using HaloAgent.Engine.Abstractions;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Server.Channels;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Server;

/// <summary>
/// 帧服务主循环：收一帧 → 分派 → 回一帧（或推一串事件）。
/// P0 只实现 system.* 方法。P1 会在这里接入 MethodRouter，
/// P2 会接入 AgentSession（真正的 agent 循环）。
/// 并发模型（重要）：
///   - 读循环是**单线程顺序**的：保证同一连接上请求的到达顺序可预期。
///   - P0 为了简单，先做顺序处理；P1 再引入 worker 池（到时会有测试验证并发）。
///   - 应答帧的写出由 channel 自己保证线程安全。
/// </summary>
public sealed class FrameServer
{
    private readonly IFrameChannel _channel;
    private readonly ILogger _log;
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;

    public FrameServer(IFrameChannel channel, ILogger logger)
    {
        _channel = channel;
        _log = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("帧服务开始，通道={Channel}", _channel.Describe);

        while (!ct.IsCancellationRequested)
        {
            Frame? frame;
            try
            {
                frame = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "读帧失败，通道可能已断开");
                break;
            }

            if (frame is null)
            {
                _log.LogInformation("对端关闭输入，退出帧服务");
                break;
            }

            switch (frame)
            {
                case RequestFrame req:
                    await HandleRequestAsync(req, ct).ConfigureAwait(false);
                    break;
                case EventFrame:
                    _log.LogWarning("引擎收到事件帧，忽略（事件只应由引擎发出）");
                    break;
                case ResponseFrame:
                    _log.LogWarning("引擎收到应答帧，忽略（应答只应由宿主侧发送）");
                    break;
            }
        }

        _log.LogInformation("帧服务结束");
    }

    private async Task HandleRequestAsync(RequestFrame req, CancellationToken ct)
    {
        _log.LogDebug("<- {Method} id={Id}", req.Method, req.Id);
        try
        {
            var result = req.Method switch
            {
                "system.ping" => HandlePing(),
                "system.info" => HandleInfo(),
                "system.protocol" => HandleProtocol(),
                _ => throw new RpcException(
                    "method.notFound",
                    $"未知方法: {req.Method}",
                    "检查帧的 m 字段拼写；用 system.ping 做连通性测试"),
            };

            var res = ResponseFrame.Success(req.Id, result, HaloJson.Options);
            await _channel.SendAsync(res, ct).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            _log.LogWarning("请求 {Method} 失败: {Code} {Message}", req.Method, ex.Code, ex.Message);
            await _channel.SendAsync(
                ResponseFrame.Failure(req.Id, ex.Code, ex.Message), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "请求 {Method} 未预期异常", req.Method);
            await _channel.SendAsync(
                ResponseFrame.Failure(req.Id, "internal", ex.Message), ct).ConfigureAwait(false);
        }
    }

    private object HandlePing() => new
    {
        pong = true,
        version = HaloVersion.Engine,
        protocol = HaloVersion.ProtocolVersion,
        pid = Environment.ProcessId,
        uptimeMs = (long)(DateTimeOffset.Now - StartedAt).TotalMilliseconds,
        serverTimeMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
    };

    private object HandleInfo() => new
    {
        version = HaloVersion.Engine,
        protocol = HaloVersion.ProtocolVersion,
        pid = Environment.ProcessId,
        osVersion = Environment.OSVersion.Version.ToString(),
        is64BitOs = Environment.Is64BitOperatingSystem,
        is64BitProcess = Environment.Is64BitProcess,
        runtime = Environment.Version.ToString(),
        dataDir = EngineStartupOptions.DefaultDataDir(),
        startedAtMs = StartedAt.ToUnixTimeMilliseconds(),
    };

    private object HandleProtocol() => new
    {
        version = HaloVersion.ProtocolVersion,
        encoding = "ndjson",
        framing = "line",
    };
}

/// <summary>可以带"人读提示"的 RPC 错误。前端能直接把 Hint 展示给用户。</summary>
public sealed class RpcException : Exception
{
    public string Code { get; }
    public string? Hint { get; }
    public RpcException(string code, string message, string? hint = null) : base(message)
    {
        Code = code;
        Hint = hint;
    }
}
