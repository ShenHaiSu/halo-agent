using System.Text;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

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
}
