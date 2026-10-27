using System.Collections.Concurrent;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Channels;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Channels;

public sealed class StdioChannelTests : FrameChannelContractTests
{
    protected override Task<(IFrameChannel a, IFrameChannel b)> CreatePairAsync()
    {
        // 纯托管环回流（替代匿名管道）：a 写 ab、b 从 ab 读；b 写 ba、a 从 ba 读。
        // 匿名管道在 CI 沙箱里出现过 Pipe is broken 误报，托管队列无此问题，
        // 且覆盖帧切分/并发/大帧/EOF 的语义与 stdio 真实路径一致
        // （StdioChannel 只依赖 Stream.ReadAsync/WriteAsync，不依赖管道类型）。
        // 真实 OS 管道覆盖由 StdioRoundtripTests（真实进程 stdio）保证。
        var ab = new LoopbackStream();
        var ba = new LoopbackStream();

        IFrameChannel a = new OwnedStdioChannel(new StdioChannel(input: ba, output: ab), ba, ab);
        IFrameChannel b = new OwnedStdioChannel(new StdioChannel(input: ab, output: ba), ab, ba);

        return Task.FromResult((a, b));
    }

    /// <summary>
    /// 给通道补上“释放即关闭底层流”语义。只用于测试；生产保持“不关流”。
    /// </summary>
    private sealed class OwnedStdioChannel : IFrameChannel
    {
        private readonly StdioChannel _inner;
        private readonly LoopbackStream[] _owned;
        private bool _disposed;

        public OwnedStdioChannel(StdioChannel inner, params LoopbackStream[] owned)
        {
            _inner = inner;
            _owned = owned;
        }

        public string Describe => _inner.Describe;

        public ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct) =>
            _inner.ReceiveAsync(ct);

        public ValueTask SendAsync(Frame frame, CancellationToken ct) =>
            _inner.SendAsync(frame, ct);

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            foreach (var s in _owned) s.Complete();
        }
    }

    /// <summary>
    /// 阻塞队列背书的双工内存流。Write 非阻塞入队；Read 在空队列时阻塞等，
    /// Complete 后排空返回 0（EOF）。Stream 基类的 *Async(Memory) 默认实现会
    /// 走到同步 Read/Write，在测试线程池上阻塞是可接受的。
    /// </summary>
    private sealed class LoopbackStream : Stream
    {
        private readonly BlockingCollection<byte[]> _q = new();
        private byte[]? _cur;
        private int _pos;

        public void Complete() => _q.CompleteAdding();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var cp = new byte[count];
            Buffer.BlockCopy(buffer, offset, cp, 0, count);
            _q.Add(cp);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_cur is null)
            {
                try { _cur = _q.Take(); }
                catch (InvalidOperationException) { return 0; }
                _pos = 0;
            }
            int n = Math.Min(count, _cur.Length - _pos);
            Buffer.BlockCopy(_cur, _pos, buffer, offset, n);
            _pos += n;
            if (_pos >= _cur.Length) _cur = null;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(new Memory<byte>(buffer, offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_cur is null)
            {
                try { _cur = await Task.Run(() => _q.Take(ct), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (InvalidOperationException) { return 0; }
                _pos = 0;
            }
            int n = Math.Min(buffer.Length, _cur.Length - _pos);
            new Span<byte>(_cur, _pos, n).CopyTo(buffer.Span);
            _pos += n;
            if (_pos >= _cur.Length) _cur = null;
            return n;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
    }
}
