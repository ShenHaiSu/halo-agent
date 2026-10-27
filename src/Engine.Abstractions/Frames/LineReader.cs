namespace HaloAgent.Engine.Abstractions.Frames;

/// <summary>
/// 行读取器：从字节流里切出完整行（不含 \n）。
/// 为什么不用 StreamReader.ReadLineAsync：
///   1. StreamReader 会做 BOM 探测与编码探测，对字节流不透明。
///   2. 它在某些管道场景会预读并“吞掉”下一帧的字节（.NET 6 的已知行为）。
///   3. 我们自己实现才能精确控制“单帧上限”。
/// 实现要点：
///   - 内部用可增长的 byte[]（初始 4 KB，按需翻倍，上限 MaxFrameBytes）
///   - 逐块读（一次 8 KB），在块内扫描 \n，避免逐字节系统调用
///   - 遇到 \r 忽略（容忍 CRLF）
/// </summary>
public sealed class LineReader
{
    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private byte[] _buf;
    private int _len;
    private int _scanFrom;

    /// <summary>创建行读取器。</summary>
    public LineReader(Stream stream, int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes, int initialCapacity = 8192)
    {
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
        _buf = new byte[Math.Min(initialCapacity, maxFrameBytes)];
    }

    /// <summary>
    /// 读一行。返回 null 表示 EOF。
    /// 抛 FrameTooLargeException 表示单帧超限。
    /// </summary>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            // 先在已缓冲的数据里找 \n
            for (int i = _scanFrom; i < _len; i++)
            {
                if (_buf[i] == (byte)'\n')
                {
                    int lineLen = i;
                    // 去掉行尾 \r
                    if (lineLen > 0 && _buf[lineLen - 1] == (byte)'\r') lineLen--;

                    var line = new byte[lineLen];
                    Array.Copy(_buf, 0, line, 0, lineLen);

                    // 把剩余数据前移
                    int remain = _len - (i + 1);
                    if (remain > 0) Array.Copy(_buf, i + 1, _buf, 0, remain);
                    _len = remain;
                    _scanFrom = 0;
                    return line;
                }
            }
            _scanFrom = _len;

            // 缓冲满了，扩容或报错
            if (_len >= _buf.Length)
            {
                if (_buf.Length >= _maxFrameBytes)
                    throw new FrameTooLargeException(_maxFrameBytes);
                int newSize = (int)Math.Min((long)_buf.Length * 2, _maxFrameBytes);
                Array.Resize(ref _buf, newSize);
            }

            // 读一块
            int n = await _stream.ReadAsync(
                _buf.AsMemory(_len, _buf.Length - _len), ct).ConfigureAwait(false);
            if (n == 0)
            {
                // EOF：如果缓冲里还有残留（末尾没有 \n），也算一行（宽容）
                if (_len > 0)
                {
                    int lineLen = _len;
                    if (lineLen > 0 && _buf[lineLen - 1] == (byte)'\r') lineLen--;
                    var line = new byte[lineLen];
                    Array.Copy(_buf, 0, line, 0, lineLen);
                    _len = 0;
                    _scanFrom = 0;
                    return line;
                }
                return null;
            }
            _len += n;
        }
    }
}

/// <summary>单帧超过上限。</summary>
public sealed class FrameTooLargeException : Exception
{
    /// <summary>上限（字节）。</summary>
    public int Limit { get; }
    /// <summary>创建超限异常。</summary>
    public FrameTooLargeException(int limit)
        : base($"单帧超过上限 {limit / 1024 / 1024} MB，连接将被断开")
    {
        Limit = limit;
    }
}
