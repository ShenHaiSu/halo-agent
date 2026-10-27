 using System.Text.Json;
using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

public class ToolArgumentParserTests
{
    [Fact]
    public void Empty_string_parses_to_empty_object()
    {
        var parsed = ToolArgumentParser.Parse("");

        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
        Assert.False(parsed.EnumerateObject().Any());
    }

    [Fact]
    public void Null_parses_to_empty_object()
    {
        var parsed = ToolArgumentParser.Parse(null);

        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
    }

    [Fact]
    public void Missing_closing_quote_is_repaired()
    {
        var parsed = ToolArgumentParser.Parse("{\"command\":\"ls -la}");

        Assert.Equal("ls -la", parsed.GetProperty("command").GetString());
    }

    [Fact]
    public void Missing_closing_brace_is_repaired()
    {
        var parsed = ToolArgumentParser.Parse("{\"command\":\"ls -la\"");

        Assert.Equal("ls -la", parsed.GetProperty("command").GetString());
    }

    [Fact]
    public void Missing_quote_and_brace_are_repaired()
    {
        var parsed = ToolArgumentParser.Parse("{\"command\":\"ls -la");

        Assert.Equal("ls -la", parsed.GetProperty("command").GetString());
    }

    [Fact]
    public void Non_object_throws()
    {
        Assert.Throws<ToolArgumentException>(() => ToolArgumentParser.Parse("[1,2]"));
    }

    [Fact]
    public void Garbage_throws()
    {
        Assert.Throws<ToolArgumentException>(() => ToolArgumentParser.Parse("not json at all {{{"));
    }
}
