using System.Text;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>
/// SseParser 用例。
/// 约定：凡接收 byte[] 的 API，测试必须至少有 1 个用例按字节下标切分。
/// 只按 string 切分 = 在「字符边界」这个更高抽象上测试，永远测不出「字符被切开」的缺陷（D9）。
/// </summary>
public class SseParserTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static List<string> PushAll(SseParser parser, params string[] parts)
    {
        var result = new List<string>();
        foreach (var part in parts)
        {
            var bytes = Bytes(part);
            result.AddRange(parser.Push(bytes, bytes.Length));
        }
        return result;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    [Fact]
    public void Two_events_in_one_push_both_returned()
    {
        var parser = new SseParser();

        var events = PushAll(parser, "data: {\"a\":1}\n\ndata: {\"b\":2}\n\n");

        Assert.Equal(2, events.Count);
        Assert.Equal("{\"a\":1}", events[0]);
        Assert.Equal("{\"b\":2}", events[1]);
    }

    [Fact]
    public void Event_split_across_chunks_is_reassembled()
    {
        var parser = new SseParser();

        var first = PushAll(parser, "data: {\"argu");
        var second = PushAll(parser, "ments\":\"x\"}\n\n");

        Assert.Empty(first);
        Assert.Single(second);
        Assert.Equal("{\"arguments\":\"x\"}", second[0]);
    }

    [Fact]
    public void CrLf_line_endings_are_normalized()
    {
        var parser = new SseParser();

        var events = PushAll(parser, "data: hello\r\n\r\n");

        Assert.Single(events);
        Assert.Equal("hello", events[0]);
    }

    [Fact]
    public void Lone_cr_is_treated_as_line_break()
    {
        var parser = new SseParser();

        var events = PushAll(parser, "data: world\r\r");

        Assert.Single(events);
        Assert.Equal("world", events[0]);
    }

    [Fact]
    public void Sticky_packets_with_trailing_partial_are_buffered()
    {
        var parser = new SseParser();

        var events = PushAll(parser, "data: 1\n\ndata: 2\n\ndata: par");

        Assert.Equal(2, events.Count);
        Assert.Equal("1", events[0]);
        Assert.Equal("2", events[1]);

        var rest = PushAll(parser, "tial\n\n");

        Assert.Single(rest);
        Assert.Equal("partial", rest[0]);
    }

    [Fact]
    public void Finish_flushes_unterminated_tail()
    {
        var parser = new SseParser();

        var events = PushAll(parser, "data: tail");

        Assert.Empty(events);
        var flushed = new List<string>(parser.Finish());
        Assert.Single(flushed);
        Assert.Equal("tail", flushed[0]);
        Assert.Empty(parser.Finish());
    }

    // ================= D9：按字节下标切分（修前必红） =================

    [Fact]
    public void D9_multibyte_char_split_across_chunks_is_not_corrupted()
    {
        const string payload = "data: {\"t\":\"断\"}\n\n";
        var all = Bytes(payload);

        int idx = IndexOfBytes(all, Bytes("断"));
        Assert.True(idx > 0);

        foreach (var cut in new[] { idx + 1, idx + 2 })
        {
            var parser = new SseParser();
            var events = new List<string>();
            events.AddRange(parser.Push(all.AsSpan(0, cut).ToArray(), cut));
            events.AddRange(parser.Push(all.AsSpan(cut).ToArray(), all.Length - cut));
            events.AddRange(parser.Finish());

            Assert.Single(events);
            Assert.Equal("{\"t\":\"断\"}", events[0]);
            Assert.DoesNotContain('\uFFFD', events[0]);
            Assert.Equal(0, parser.ReplacementFallbackCount);
        }
    }

    [Fact]
    public void D9_byte_by_byte_push_reassembles_correctly()
    {
        const string payload = "data: {\"t\":\"中断生成\"}\n\n";
        var all = Bytes(payload);

        var parser = new SseParser();
        var events = new List<string>();
        for (int i = 0; i < all.Length; i++)
        {
            events.AddRange(parser.Push(new[] { all[i] }, 1));
        }
        events.AddRange(parser.Finish());

        Assert.Single(events);
        Assert.Equal("{\"t\":\"中断生成\"}", events[0]);
        Assert.DoesNotContain('\uFFFD', events[0]);
    }

    [Fact]
    public void D9_finish_flushes_decoder_residual()
    {
        var all = Bytes("data: tail");
        var head = all.AsSpan(0, all.Length - 1).ToArray();

        var parser = new SseParser();
        var events = new List<string>();
        events.AddRange(parser.Push(head, head.Length));
        events.AddRange(parser.Push(all.AsSpan(all.Length - 1).ToArray(), 1));
        events.AddRange(parser.Finish());

        Assert.Single(events);
        Assert.Equal("tail", events[0]);
        Assert.DoesNotContain('\uFFFD', events[0]);
    }

    [Fact]
    public void D9_finish_emits_replacement_for_truly_truncated_tail()
    {
        // 流真的在多字节字符中间截断：Finish 必须输出 U+FFFD 而不是静默丢弃。
        var all = Bytes("data: X断\n\n");
        int idx = IndexOfBytes(all, Bytes("断"));
        Assert.True(idx > 0);
        var head = all.AsSpan(0, idx + 1).ToArray();

        var parser = new SseParser();
        var events = new List<string>();
        events.AddRange(parser.Push(head, head.Length));
        events.AddRange(parser.Finish());

        Assert.Single(events);
        Assert.Contains('\uFFFD', events[0]);
    }

    [Fact]
    public void D9_crlf_split_across_chunks_does_not_double_newline()
    {
        var all = Bytes("data: a\r\n\r\ndata: b\r\n\r\n");
        int i = Array.IndexOf(all, (byte)'\r');
        Assert.True(i > 0);

        var parser = new SseParser();
        var events = new List<string>();
        events.AddRange(parser.Push(all.AsSpan(0, i + 1).ToArray(), i + 1));
        events.AddRange(parser.Push(all.AsSpan(i + 1).ToArray(), all.Length - i - 1));
        events.AddRange(parser.Finish());

        Assert.Equal(2, events.Count);
        Assert.Equal("a", events[0]);
        Assert.Equal("b", events[1]);
    }
}
