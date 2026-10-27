using System.Text.Json;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

public class ToolCallAccumulatorTests
{
    private static JsonElement Elem(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Fragments_with_same_index_are_concatenated()
    {
        var acc = new ToolCallAccumulator();
        acc.Merge(Elem("{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}"));
        acc.Merge(Elem("{\"index\":0,\"function\":{\"arguments\":\"{\\\"pa\"}}"));
        acc.Merge(Elem("{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"a.txt\\\"}\"}}"));

        var calls = acc.Finish();

        Assert.Single(calls);
        Assert.Equal("call_1", calls[0].Id);
        Assert.Equal("read_file", calls[0].Function.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", calls[0].Function.Arguments);
    }

    [Fact]
    public void Missing_id_generates_call_prefix_with_name()
    {
        var acc = new ToolCallAccumulator();
        acc.Merge(Elem("{\"index\":0,\"function\":{\"name\":\"run\",\"arguments\":\"{}\"}}"));

        var calls = acc.Finish();

        Assert.Single(calls);
        Assert.Equal("call_run", calls[0].Id);
    }

    [Fact]
    public void Nameless_fragment_is_dropped()
    {
        var acc = new ToolCallAccumulator();
        acc.Merge(Elem("{\"index\":0,\"id\":\"x\",\"function\":{\"arguments\":\"{}\"}}"));

        Assert.Empty(acc.Finish());
    }

    [Fact]
    public void Object_arguments_are_serialized()
    {
        var acc = new ToolCallAccumulator();
        acc.Merge(Elem("{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"f\",\"arguments\":{\"a\":1}}}"));

        var calls = acc.Finish();

        Assert.Single(calls);
        Assert.Contains("a", calls[0].Function.Arguments, StringComparison.Ordinal);
    }
}
