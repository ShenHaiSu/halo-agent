using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Serialization;

namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 引擎客户端（P0 版：通过 stdio 子进程通信；P1 换成 Named Pipe）。
/// 职责：
///   1. 启动/停止引擎进程
///   2. 请求-应答配对（id → TaskCompletionSource）
///   3. 事件分发（订阅者回调）
///   4. 异常退出检测与上报
/// 线程模型：
///   - 每条请求都返回 Task，绝不阻塞调用方（CEF 的 IO 线程不能被阻塞）。
///   - 读循环跑在独立后台任务上。
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private long _nextId;
    private Process? _proc;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private volatile bool _disposing;

    /// <summary>事件回调（seq, kind, payload）</summary>
    public event Action<long, string, JsonElement?>? EventReceived;

    /// <summary>引擎进程意外退出</summary>
    public event Action<int>? ProcessExited;

    public bool IsRunning => _proc is { HasExited: false };

    public async Task StartAsync(string dataDir, CancellationToken ct = default)
    {
        var exe = LocateEngineExe();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 关键：必须用不带 BOM 的 UTF-8。Encoding.UTF8 经 StreamWriter 写出时
            // 会带 EF BB BF 前导，引擎的帧解析会失败（HaloJson 侧虽已容忍 BOM，
            // 但其它实现/历史版本不容忍；且首帧带 BOM 本就不合 NDJSON 约定）。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--stdio");
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动引擎进程");

        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = _proc.ExitCode; } catch { }
            // 宿主主动释放（DisposeAsync）导致的退出不算异常；只记 Info。
            // 只有非 0 退出码且非主动释放时才记 Error。
            if (_disposing || code == 0) AppLog.Info($"引擎进程退出，退出码 {code}");
            else AppLog.Error($"引擎进程异常退出，退出码 {code}");
            ProcessExited?.Invoke(code);
        };

        // stderr 单独读，写日志（不混进帧流）
        _ = Task.Run(() => PumpStderrAsync(_proc.StandardError, _cts.Token));

        _readLoop = Task.Run(() => ReadLoopAsync(_proc.StandardOutput, _cts.Token));

        // 握手：确认真的活着
        var pong = await RequestAsync<JsonElement>("system.ping", null, ct).ConfigureAwait(false);
        AppLog.Info($"引擎已就绪: {pong}");
    }

    public async Task<T?> RequestAsync<T>(string method, object? prms, CancellationToken ct = default)
    {
        if (_proc is null || _proc.HasExited)
            throw new InvalidOperationException("引擎未运行");

        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = prms is null
            ? $"{{\"t\":\"req\",\"id\":{id},\"m\":{JsonSerializer.Serialize(method)}}}\n"
            : $"{{\"t\":\"req\",\"id\":{id},\"m\":{JsonSerializer.Serialize(method)},\"p\":{JsonSerializer.Serialize(prms, HaloJson.Options)}}}\n";

        try
        {
            await _proc.StandardInput.WriteAsync(payload).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException($"写入引擎失败: {ex.Message}", ex);
        }

        // 给个超时兜底：引擎卡死时不能永远挂着
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var result = await tcs.Task.ConfigureAwait(false);
        return result is null ? default : JsonSerializer.Deserialize<T>(result.Value.GetRawText(), HaloJson.Options);
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var dto = HaloJson.ReadFrame(line);
                switch (dto)
                {
                    case FrameDto.Response res:
                        if (_pending.TryRemove(res.Id, out var tcs))
                        {
                            if (res.Ok) tcs.TrySetResult(res.Result);
                            else
                            {
                                var msg = res.Error?.ToString() ?? "未知错误";
                                tcs.TrySetException(new InvalidOperationException(msg));
                            }
                        }
                        break;
                    case FrameDto.Event ev:
                        try { EventReceived?.Invoke(ev.Seq, ev.Kind, ev.Payload); }
                        catch (Exception ex) { AppLog.Warn("事件回调异常: " + ex.Message); }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) AppLog.Error("引擎读循环异常", ex);
        }
        finally
        {
            // 所有挂起的请求都失败，避免调用方永远等待
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new InvalidOperationException("引擎连接已关闭"));
        }
    }

    private async Task PumpStderrAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
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
        var candidates = new[]
        {
            Path.Combine(baseDir, "HaloAgent.Engine.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Engine.Server", "bin", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Engine.Server", "bin", "Release", "net6.0-windows", "win-x86", "HaloAgent.Engine.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Engine.Server", "bin", "x86", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Engine.Server", "bin", "x64", "Release", "net6.0-windows", "HaloAgent.Engine.exe")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        throw new FileNotFoundException(
            "找不到 HaloAgent.Engine.exe。请确认发布目录完整，或先构建 Engine.Server。");
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        try
        {
            _cts?.Cancel();
            if (_proc is { HasExited: false })
            {
                try
                {
                    _proc.StandardInput.Close();
                    if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true);
                }
                catch { }
            }
            _proc?.Dispose();
            if (_readLoop is not null) await Task.WhenAny(_readLoop, Task.Delay(1000)).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            _cts?.Dispose();
        }
    }
}
