using System.Text;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// SSE 解析器。
///
/// ============ 核心难点 ============
/// 一个 SSE 事件（`data: {...}\n\n`）可能被 TCP 拆成多个 chunk 到达。
/// 如果按 chunk 独立 split('\n')，后半段（没有 data: 前缀）会被丢掉，
/// 结果是**工具 arguments 被截断**——这是最难查的一类 bug（因为只在弱网/大 payload 时出现）。
///
/// 解决：跨 chunk 缓冲。只有看到完整的事件分隔符（\n\n）才产出一条。
///
/// ============ D9 补充（多字节字符跨 chunk）============
/// `Stream.ReadAsync` 的返回长度由网络层决定，与 UTF-8 字符边界无关。
/// 若每块用 `Encoding.UTF8.GetString` 独立解码，切在中文/emoji 中间的字节会变成 U+FFFD，
/// 且原始字节在解码时即被丢弃、后续无法补救。因此这里用跨 chunk 复用的
/// `Decoder`（`flush: false` 中途暂存碎字节，`Finish()` 才 `flush: true`）。
///
/// 测试约定：凡接收 `byte[]` 的 API，测试必须至少有 1 个用例按字节下标切分。
/// 只按 string 切分 = 在「字符边界」这个更高抽象上测试，永远测不出「字符被切开」的缺陷。
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _chars = new char[1];
    private bool _pendingCr;

    /// <summary>D9 可观测：累计见过的 U+FFFD 替换符个数（含流内非法字节与尾部截断 flush 产生）。</summary>
    /// <remarks>正常中文流修后应为 0；若线上大于 0，说明仍有别的解码点漏网。</remarks>
    public int ReplacementFallbackCount { get; private set; }

    /// <summary>送入一块数据，返回其中完整事件的 data 负载。</summary>
    /// <remarks>
    /// 不用 ReadOnlySpan&lt;byte&gt; 参数：迭代器方法不能带 ref struct 参数（CS4013）。
    /// 注意：解码在本方法入口同步完成（不放在 yield 之后），避免调用方中途 break 导致 Decoder 状态错位。
    /// </remarks>
    public IEnumerable<string> Push(byte[] chunk, int count)
    {
        DecodeInto(chunk, count);
        return SplitEvents(flushTail: false);
    }

    /// <summary>流结束冲刷（个别服务端不补最后的空行）。必须 flush 解码器，否则尾部残字节会被静默丢弃。</summary>
    public IEnumerable<string> Finish()
    {
        FlushDecoder();
        return SplitEvents(flushTail: true);
    }

    private void DecodeInto(byte[] chunk, int count)
    {
        if (chunk is null) throw new ArgumentNullException(nameof(chunk));
        if (count < 0 || count > chunk.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;

        int max = Encoding.UTF8.GetMaxCharCount(count);
        EnsureCharCapacity(max);
        // flush:false —— 不完整的多字节序列留在 Decoder 内部，等下一块补完（中途禁止 flush，避免碎字节变 U+FFFD）。
        int c = _decoder.GetChars(chunk, 0, count, _chars, 0, flush: false);
        if (c <= 0) return;
        for (int i = 0; i < c; i++)
        {
            if (_chars[i] == '\uFFFD')
                ReplacementFallbackCount++;
        }
#if DEBUG
        if (c > 0 && _chars.AsSpan(0, c).IndexOf('\uFFFD') >= 0)
            System.Diagnostics.Debug.WriteLine($"[sse] U+FFFD decoded in Push bytes={count}");
#endif
        AppendNormalized(new string(_chars, 0, c));
    }

    private void FlushDecoder()
    {
        // flush:true 把 Decoder 里残留的字节吐出来（此时变 U+FFFD 是正确的：流真的结束了）。
        int max = _decoder.GetCharCount(Array.Empty<byte>(), 0, 0, flush: true);
        EnsureCharCapacity(max);
        int c = _decoder.GetChars(Array.Empty<byte>(), 0, 0, _chars, 0, flush: true);
        if (c > 0)
        {
            for (int i = 0; i < c; i++)
            {
                if (_chars[i] == '\uFFFD')
                    ReplacementFallbackCount++;
            }
            AppendNormalized(new string(_chars, 0, c));
        }
        _pendingCr = false;
    }

    private void EnsureCharCapacity(int need)
    {
        if (need <= 0) return;
        if (_chars.Length >= need) return;
        Array.Resize(ref _chars, Math.Max(need, _chars.Length * 2));
    }

    /// <summary>归一化换行：\r\n → \n，孤立 \r → \n；处理跨块的 \r（上一块以 \r 结尾、下一块以 \n 开头只算一个换行）。</summary>
    private void AppendNormalized(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c == '\n') continue; // \r\n → 吃掉 \n，只保留之前已输出的那个 \n
            }
            if (c == '\r')
            {
                _pendingCr = true;
                _buffer.Append('\n');
                continue;
            }
            _buffer.Append(c);
        }
    }

    private IEnumerable<string> SplitEvents(bool flushTail)
    {
        var text = _buffer.ToString();
        int cursor = 0;

        while (true)
        {
            int sep = text.IndexOf("\n\n", cursor, StringComparison.Ordinal);
            if (sep < 0) break;

            foreach (var payload in ExtractDataLines(text[cursor..sep]))
                yield return payload;

            cursor = sep + 2;
        }

        _buffer.Clear();
        if (cursor < text.Length) _buffer.Append(text[cursor..]);

        if (flushTail && _buffer.Length > 0)
        {
            var tail = _buffer.ToString();
            _buffer.Clear();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                foreach (var payload in ExtractDataLines(tail))
                    yield return payload;
            }
        }
    }

    private static IEnumerable<string> ExtractDataLines(string block)
    {
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.AsSpan();
            // SSE 规范：以 "data:" 开头的行是负载；其余（event:/id:/retry:/注释）忽略
            if (!line.StartsWith("data:")) continue;

            var payload = line[5..].TrimStart();
            if (payload.Length > 0) yield return payload.ToString();
        }
    }
}
