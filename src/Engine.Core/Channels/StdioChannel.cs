using System.Buffers;
using HaloAgent.Engine.Abstractions.Frames;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Core.Channels;

/// <summary>
/// stdin/stdout 帧通道。
///
/// 用途（长期保留，不是临时脚手架）：
///   1. CI 的引擎测试（无需建管道、无需起进程管理器）
///   2. 手工调试：echo '{...}' | HaloAgent.Engine.exe --stdio
///   3. 第三方工具集成（可用脚本驱动引擎）
///
/// ⚠️ 铁律：stdout 归帧独占。任何日志必须写 stderr 或文件。
///    一旦有日志混进 stdout，宿主解析第一行就会失败。
/// </summary>
public sealed class StdioChannel : IFrameChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LineReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ILogger? _log;
    private bool _disposed;

    public StdioChannel(Stream? input = null, Stream? output = null, ILogger? log = null)
    {
        _input = input ?? Console.OpenStandardInput();
        _output = output ?? Console.OpenStandardOutput();
        _log = log;
        _reader = new LineReader(_input);
    }

    public string Describe => "stdio";

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
            return ReceiveResult.Malformed(ex.Message);
        }
        catch (IOException ex)
        {
            _log?.LogDebug("stdin 读取结束: {Msg}", ex.Message);
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
            await _output.WriteAsync(buf.WrittenMemory, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // 不关 Console 标准流（进程内可能复用）；只释放锁
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
