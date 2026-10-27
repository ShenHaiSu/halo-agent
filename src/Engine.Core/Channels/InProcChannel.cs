using System.Threading.Channels;
using HaloAgent.Engine.Abstractions.Frames;

namespace HaloAgent.Engine.Core.Channels;

/// <summary>
/// 同进程通道：两个有界 Channel 队列直连，不做序列化。
///
/// 为什么需要它：
///   1. P2 的 agent 循环单测：不关心传输，只想快速驱动引擎。
///      用 NamedPipe 每次测试要建管道 + 起进程，慢且不稳。
///   2. 单进程调试模式（宿主内嵌引擎）：省掉序列化与 IPC。
///
/// ⚠️ 局限：不经过 JSON 序列化，所以**不能**验证"线上格式正确性"。
///    契约测试里必须额外跑一遍序列化往返（FrameCodec.Write → Parse）。
///
/// 有界容量默认 1024：满时 SendAsync 会等待（提供自然背压）。
/// </summary>
public sealed class InProcChannel : IFrameChannel
{
    private readonly ChannelWriter<Frame> _out;
    private readonly ChannelReader<Frame> _in;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private InProcChannel(ChannelWriter<Frame> outWriter, ChannelReader<Frame> inReader, string describe)
    {
        _out = outWriter;
        _in = inReader;
        Describe = describe;
    }

    public string Describe { get; }

    /// <summary>
    /// 创建一对互连的通道。
    /// 返回 (a, b)：a.Send → b.Receive；b.Send → a.Receive。
    /// </summary>
    public static (InProcChannel a, InProcChannel b) CreatePair(int capacity = 1024)
    {
        var ab = Channel.CreateBounded<Frame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var ba = Channel.CreateBounded<Frame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var a = new InProcChannel(ab.Writer, ba.Reader, "inproc:a");
        var b = new InProcChannel(ba.Writer, ab.Reader, "inproc:b");
        return (a, b);
    }

    public async ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        try
        {
            var frame = await _in.ReadAsync(ct).ConfigureAwait(false);
            return ReceiveResult.Ok(frame);
        }
        catch (ChannelClosedException)
        {
            return ReceiveResult.Closed();
        }
    }

    public async ValueTask SendAsync(Frame frame, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _out.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // 对端已关闭：静默丢弃（与管道写失败语义一致）
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
        _out.TryComplete();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
