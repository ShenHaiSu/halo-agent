using System.Buffers;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HaloAgent.Engine.Abstractions.Frames;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Core.Channels;

/// <summary>
/// Named Pipe 帧通道。生产形态：宿主 ↔ 引擎两个进程通过它通信。
///
/// 安全模型（这是本方案相对 WebSocket 的最大优势）：
///   管道通过 PipeSecurity 限定为"仅当前用户 + SYSTEM + 管理员"，
///   其他用户/进程根本连不上。所以**不需要** token、不需要 Origin 校验、
///   不需要 HTTP 头检查——一整套浏览器侧的防护在这里不存在。
///
/// 帧格式：与 stdio 完全一致（NDJSON，一行一帧），
///         这让 "Engine.Cli --stdio" 能作为真实测试入口。
///
/// 生命周期：
///   服务端（引擎）：CreateServerStream() → AcceptAsync() → 用
///   客户端（宿主）：ConnectAsync()
/// </summary>
public sealed class NamedPipeChannel : IFrameChannel
{
    private readonly PipeStream _pipe;
    private readonly ILogger? _log;
    private readonly LineReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private NamedPipeChannel(PipeStream pipe, string describe, ILogger? log, int maxFrameBytes)
    {
        _pipe = pipe;
        Describe = describe;
        _log = log;
        _reader = new LineReader(pipe, maxFrameBytes);
    }

    public string Describe { get; }

    // ============================================================
    //  工厂：服务端（引擎侧）
    // ============================================================

    /// <summary>
    /// 创建一个尚未连接的管道服务端。
    /// 引擎侧应该在循环里不断调用它，以便宿主断开后能重连。
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(
        string pipeName, int bufferSize = 64 * 1024)
    {
        var security = BuildPipeSecurity();
        // .NET 6 的 NamedPipeServerStream 构造器不支持 PipeSecurity，
        // 必须经 NamedPipeServerStreamAcl.Create 传入 ACL（否则 Everyone 可读）。
        return System.IO.Pipes.NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            bufferSize,
            bufferSize,
            security,
            System.IO.HandleInheritability.None,
            (System.IO.Pipes.PipeAccessRights)0);
    }

    /// <summary>服务端等待连接并包装成通道。</summary>
    public static async Task<NamedPipeChannel> AcceptAsync(
        string pipeName, ILogger? log = null, CancellationToken ct = default)
    {
        var server = CreateServerStream(pipeName);
        try
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            log?.LogInformation("管道已连接: {Pipe}", pipeName);
            return new NamedPipeChannel(server, $"pipe:{pipeName}", log, FrameCodec.DefaultMaxFrameBytes);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    // ============================================================
    //  工厂：客户端（宿主侧）
    // ============================================================

    /// <summary>
    /// 连接到引擎的管道。带超时（引擎可能还没起监听）。
    /// </summary>
    public static async Task<NamedPipeChannel> ConnectAsync(
        string pipeName, int timeoutMs = 5000, ILogger? log = null, CancellationToken ct = default)
    {
        var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await client.ConnectAsync(linked.Token).ConfigureAwait(false);
            log?.LogInformation("已连接管道: {Pipe}", pipeName);
            return new NamedPipeChannel(client, $"pipe:{pipeName}", log, FrameCodec.DefaultMaxFrameBytes);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接管道 {pipeName} 超时（{timeoutMs}ms）");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ============================================================
    //  ACL
    // ============================================================

    /// <summary>
    /// 构建管道安全描述符。
    ///
    /// 必须显式设置，不能依赖默认值：
    /// NamedPipeServerStream 默认继承父进程的令牌，
    /// 在某些配置下 Everyone 可读，这是不可接受的信息泄漏面。
    ///
    /// 规则：当前用户 FullControl + SYSTEM FullControl + Administrators ReadWrite，
    /// 不含 Everyone。
    /// </summary>
    public static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();

        // 当前用户：完全控制
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        // SYSTEM：完全控制（服务/更新程序可能需要）
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Administrators：读写（便于运维诊断，但不给删除/改权限）
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return security;
    }

    // ============================================================
    //  帧收发（三态）
    // ============================================================

    public async ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);

        byte[]? line;
        try
        {
            line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (FrameTooLargeException ex)
        {
            _log?.LogError(ex, "帧超限，断开连接");
            return ReceiveResult.Malformed(ex.Message);
        }
        catch (IOException ex)
        {
            _log?.LogDebug("管道读失败（多为对端关闭）: {Msg}", ex.Message);
            return ReceiveResult.Closed();
        }

        if (line is null) return ReceiveResult.Closed();

        var frame = FrameCodec.Parse(line, out var err);
        return frame is null
            ? ReceiveResult.Malformed(err ?? "未知解析错误")
            : ReceiveResult.Ok(frame);
    }

    public async ValueTask SendAsync(Frame frame, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);

        var buf = new ArrayBufferWriter<byte>(256);
        FrameCodec.Write(buf, frame);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(buf.WrittenMemory, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_pipe is NamedPipeServerStream ns && ns.IsConnected) { try { ns.Disconnect(); } catch { } }
        }
        catch { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
