using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Core.Channels;

namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 引擎客户端：宿主侧唯一的引擎访问入口。
///
/// 职责：
///   1. 启动/停止引擎子进程
///   2. 请求-应答配对（id → TaskCompletionSource）
///   3. 事件接收（写入环形缓冲，供前端 poll）
///   4. 异常检测（进程退出 / 管道断开）
///
/// ============ 线程模型 ============
///   - 所有公开方法都是 async，不阻塞调用方
///   - 读循环在独立 Task 上
///   - 事件环形缓冲用 lock 保护（读写都在不同线程）
///   - requestId 用 Interlocked 生成
///
/// ============ 超时 ============
///   默认 30s。特殊方法（tool.* 60s、session.compact 120s、update.* 180s）
///   通过 TimeoutFor(method) 覆盖。超时后：
///     - TaskCompletionSource 被设为 TimeoutException
///     - 该请求的 pending 项被移除（避免泄漏）
///     - **不中断连接**（后续请求继续可用）
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly EventRingBuffer _eventBuffer = new(capacity: 4096);
    private readonly object _lifecycleLock = new();

    private Process? _proc;
    private IFrameChannel? _channel;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private long _nextId;
    private volatile bool _disposed;

    /// <summary>引擎进程意外退出（含退出码）。</summary>
    public event Action<int>? ProcessExited;

    /// <summary>连接断开（对端关闭 / 读失败）。</summary>
    public event Action<string>? ConnectionLost;

    private WindowsJob? _job;
    public bool IsRunning => _channel is not null && !_disposed;


    /// <summary>P3 工厂入口：按管道名构造（供 EngineSupervisor 的 clientFactory 使用）。</summary>
    public EngineClient(string pipeName) { PipeName = pipeName; }

    public EngineClient() { }

    /// <summary>构造时绑定的管道名（可为空）。</summary>
    public string? PipeName { get; }

    /// <summary>是否已连接（P3 桥接 isReady 用）。</summary>
    public bool IsConnected => IsRunning;

    /// <summary>尝试连接（已连接返回 true；P3 Supervisor 的轮询入口）。</summary>
    public Task<bool> TryConnectAsync(CancellationToken ct = default) =>
        Task.FromResult(IsRunning);

    /// <summary>原始请求（P3 AgentBridge 用）：返回 JsonElement 结果。</summary>
    public async Task<object?> RequestRawAsync(string method, JsonElement? prms, CancellationToken ct = default)
        => await RequestAsync<JsonElement>(method, prms, ct).ConfigureAwait(false);

    /// <summary>拉事件元组版（P3 AgentBridge 用）。</summary>
    public (IReadOnlyList<HaloAgent.Engine.Abstractions.Frames.EventFrame> Events, long Next) DrainEvents(long sinceSeq, int max = 500)
    {
        var r = PollEvents(sinceSeq, max);
        return (r.Frames, r.NextSeq);
    }

    // ============================================================
    //  启动 / 停止
    // ============================================================

    /// <summary>
    /// 启动引擎进程并连接管道。
    ///
    /// 参数 pipeName 由调用方生成（格式 haloagent-{宿主PID}-{8位随机}），
    /// 这样引擎不需要"先启动再告诉我名字"的额外握手。
    /// </summary>
    public async Task StartAsync(string pipeName, string dataDir, int connectTimeoutMs = 8000)
    {
        lock (_lifecycleLock)
        {
            if (_channel is not null)
                throw new InvalidOperationException("引擎已在运行");
        }

        var exe = LocateEngineExe();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--pipe-name");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            AppLog.Error($"引擎进程退出，退出码 {code}");
            ProcessExited?.Invoke(code);
        };

        if (!proc.Start())
            throw new InvalidOperationException("无法启动引擎进程");
        try
        {
            _job = WindowsJob.CreateKillOnClose();
            _job.Assign(proc);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法把引擎加入 Job Object（宿主崩溃时可能残留引擎进程）：{ex.Message}");
        }


        // stderr 单独读，写日志（引擎诊断信息）
        _ = Task.Run(() => PumpStderrAsync(proc));

        // 连接管道（引擎可能需要几百毫秒才起监听）
        NamedPipeChannel? ch = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(connectTimeoutMs);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                ch = await NamedPipeChannel.ConnectAsync(pipeName, timeoutMs: 1500)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (proc.HasExited)
                    throw new InvalidOperationException(
                        $"引擎进程在连接前退出（退出码 {proc.ExitCode}），无法连接管道", lastError);
                await Task.Delay(200).ConfigureAwait(false);
            }
        }

        if (ch is null)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"连接引擎管道超时（{connectTimeoutMs}ms）。最后一次错误：{lastError?.Message}");
        }

        lock (_lifecycleLock)
        {
            _proc = proc;
            _channel = ch;
            _readCts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(ch, _readCts.Token));
        }

        AppLog.Info($"引擎已连接，管道 {pipeName}，pid {proc.Id}");

        // 握手：确认协议版本一致
        var pong = await RequestAsync<PongPayload>("system.ping", (object?)null).ConfigureAwait(false);
        if (pong is null || !pong.Pong)
            throw new InvalidOperationException("引擎握手失败（system.ping 未返回 pong）");
        AppLog.Info($"引擎版本 {pong.Version}，协议 {pong.Protocol}");
    }

    /// <summary>
    /// P0 兼容入口：只给数据目录，管道名自动生成。
    /// Program.cs 在切到 EngineSupervisor 之前继续用它。
    /// </summary>
    public Task StartAsync(string dataDir, CancellationToken ct = default)
        => StartAsync(EngineSupervisor.BuildPipeName(), dataDir);

    private sealed record PongPayload(bool Pong, string Version, int Protocol);

    // ============================================================
    //  请求
    // ============================================================

    public Task<T?> RequestAsync<T>(string method, object? prms, CancellationToken ct = default)
        => RequestAsync<T>(method, prms is null
            ? null
            : JsonSerializer.SerializeToElement(prms, HaloJson.Options), ct);

    public async Task<T?> RequestAsync<T>(
        string method, JsonElement? prms, CancellationToken ct = default)
    {
        var ch = _channel ?? throw new InvalidOperationException("引擎未连接");
        long id = Interlocked.Increment(ref _nextId);

        var tcs = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutMs = TimeoutFor(method);
        _pending[id] = new PendingRequest(id, tcs, DateTime.UtcNow.AddMilliseconds(timeoutMs));

        try
        {
            await ch.SendAsync(new RequestFrame { Id = id, Method = method, Params = prms }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException($"写入引擎失败: {ex.Message}", ex);
        }

        // 超时分级等待：超时只清理自己的 pending 项并抛 TimeoutException，
        // 连接保持可用；用户取消则透出 OperationCanceledException。
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);

        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"请求 {method} 超时（{timeoutMs}ms）");
        }

        var res = await tcs.Task.ConfigureAwait(false);
        if (!res.Ok)
        {
            var err = res.Error;
            throw new RpcException(
                err?.Code ?? RpcError.Internal,
                err?.Msg ?? "未知错误",
                err?.Hint);
        }

        return res.Result is null
            ? default
            : JsonSerializer.Deserialize<T>(res.Result.Value.GetRawText(), HaloJson.Options);
    }

    private static int TimeoutFor(string method) => method switch
    {
        "session.compact" => 180_000,
        "tool.undoRollback" => 60_000,
        "update.check" or "update.install" => 180_000,
        "settings.probeModels" => 45_000,
        _ => 30_000,
    };

    // ============================================================
    //  事件
    // ============================================================

    /// <summary>
    /// 拉取自 sinceSeq 之后的事件（最多 max 条）。
    /// 语义：返回的所有帧的 seq 都 > sinceSeq。
    /// 若返回的帧数与"缺口"不匹配，前端应触发 resync。
    /// </summary>
    public EventPollResult PollEvents(long sinceSeq, int max = 500)
        => _eventBuffer.Poll(sinceSeq, max);

    public sealed record EventPollResult(
        IReadOnlyList<EventFrame> Frames,
        long NextSeq,
        bool HasGap,
        long DroppedEstimate);

    // ============================================================
    //  内部
    // ============================================================

    private async Task ReadLoopAsync(IFrameChannel ch, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReceiveResult r;
                try
                {
                    r = await ch.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (r.IsClosed)
                {
                    AppLog.Info("引擎管道已关闭");
                    ConnectionLost?.Invoke("引擎连接已关闭");
                    break;
                }
                if (r.IsMalformed)
                {
                    AppLog.Warn($"收到坏帧并忽略: {r.Error}");
                    continue;
                }

                switch (r.Frame)
                {
                    case ResponseFrame res:
                        if (_pending.TryRemove(res.Id, out var p))
                            p.Tcs.TrySetResult(res);
                        else
                            AppLog.Warn($"收到未知 id 的应答: {res.Id}");
                        break;

                    case EventFrame ev:
                        _eventBuffer.Add(ev);
                        break;

                    case RequestFrame:
                        AppLog.Warn("宿主收到请求帧，忽略（请求只应由宿主发出）");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("引擎读循环异常", ex);
            ConnectionLost?.Invoke(ex.Message);
        }
        finally
        {
            // 所有挂起请求立即失败（不挂死）
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var p))
                    p.Tcs.TrySetException(new InvalidOperationException("引擎连接已关闭"));
            }
        }
    }

    private static async Task PumpStderrAsync(Process proc)
    {
        try
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                AppLog.Info($"[engine] {line}");
            }
        }
        catch { }
    }

    private static string LocateEngineExe()
    {
        // 优先同目录（发布态）；其次从源码布局推断（开发态）。
        // 开发态输出目录随构建方式而变（单项目构建 vs sln 按平台构建），逐个试。
        var baseDir = AppContext.BaseDirectory;
        var devServer = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "Engine.Server"));
        var candidates = new[]
        {
            Path.Combine(baseDir, "HaloAgent.Engine.exe"),
            Path.Combine(devServer, "bin", "Debug", "net6.0-windows", "HaloAgent.Engine.exe"),
            Path.Combine(devServer, "bin", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
            Path.Combine(devServer, "bin", "Release", "net6.0-windows", "win-x86", "HaloAgent.Engine.exe"),
            Path.Combine(devServer, "bin", "x86", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
            Path.Combine(devServer, "bin", "x64", "Release", "net6.0-windows", "HaloAgent.Engine.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        throw new FileNotFoundException(
            "找不到 HaloAgent.Engine.exe。请确认发布目录完整，或先构建 Engine.Server。");
    }

    private sealed record PendingRequest(
        long Id, TaskCompletionSource<ResponseFrame> Tcs, DateTime Deadline);

    // ============================================================
    //  释放
    // ============================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Process? proc;
        IFrameChannel? ch;
        Task? readLoop;
        CancellationTokenSource? cts;

        lock (_lifecycleLock)
        {
            proc = _proc; ch = _channel; readLoop = _readLoop; cts = _readCts;
            _proc = null; _channel = null; _readLoop = null; _readCts = null;
        }
        WindowsJob? job;
        lock (_lifecycleLock) { job = _job; _job = null; }
        try { job?.Dispose(); } catch { /* 释放失败可忽略 */ }

        // 先尝试优雅关闭
        if (ch is not null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(2000);
                await ch.SendAsync(new RequestFrame
                {
                    Id = Interlocked.Increment(ref _nextId),
                    Method = "system.shutdown",
                }, shutdownCts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        cts?.Cancel();
        if (ch is not null) await ch.DisposeAsync().ConfigureAwait(false);
        if (readLoop is not null)
            await Task.WhenAny(readLoop, Task.Delay(1500)).ConfigureAwait(false);

        if (proc is not null)
        {
            try
            {
                if (!proc.WaitForExit(3000)) proc.Kill(entireProcessTree: true);
            }
            catch { }
            proc.Dispose();
        }
        cts?.Dispose();
        AppLog.Info("EngineClient 已释放");
    }
}
