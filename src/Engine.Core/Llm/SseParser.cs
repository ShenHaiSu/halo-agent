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
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _buffer = new();

    /// <summary>送入一块数据，返回其中完整事件的 data 负载。</summary>
    /// <remarks>不用 ReadOnlySpan&lt;byte&gt; 参数：迭代器方法不能带 ref struct 参数（CS4013）。</remarks>
    public IEnumerable<string> Push(byte[] chunk, int count)
    {
        // 统一换行为 \n（兼容 \r\n，也处理跨 chunk 的 \r）
        _buffer.Append(Encoding.UTF8.GetString(chunk, 0, count).Replace("\r\n", "\n").Replace('\r', '\n'));

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

        // 保留未闭合部分
        _buffer.Clear();
        if (cursor < text.Length) _buffer.Append(text[cursor..]);
    }

    /// <summary>流结束冲刷（个别服务端不补最后的空行）。</summary>
    public IEnumerable<string> Finish()
    {
        var text = _buffer.ToString();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (var payload in ExtractDataLines(text))
            yield return payload;
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
