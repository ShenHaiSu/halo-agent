using System.Text.Json;
using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

public sealed class ToolArgumentsTests
{
    private static JsonElement SchemaOf(string toolName)
        => ToolRegistry.CreateBuiltin().Get(toolName)!.ParameterSchema;

    private static JsonElement Obj(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Missing_required_string_reports_dotted_path()
    {
        var schema = SchemaOf("edit");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("edit", schema, Obj("{\"path\":\"a.txt\"}")));
        Assert.StartsWith("工具 \"edit\" 参数校验失败:", ex.Message);
        Assert.Contains("  - old_string: 缺失必填参数", ex.Message);
        Assert.Contains("收到的参数:", ex.Message);
    }

    [Fact]
    public void Wrong_type_reports_expected_and_actual()
    {
        var schema = SchemaOf("edit");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("edit", schema, Obj("{\"path\":42,\"old_string\":\"a\",\"new_string\":\"b\"}")));
        Assert.Contains("参数校验失败:", ex.Message);
        Assert.Contains("path: 期望 string，实际 number", ex.Message);
        Assert.DoesNotContain("不能为空", ex.Message);
    }

    [Fact]
    public void Numeric_string_is_coerced_to_integer()
    {
        var schema = SchemaOf("read_file");
        var args = ToolArguments.Bind("read_file", schema, Obj("{\"path\":\"a.txt\",\"limit\":\"3\"}"));
        Assert.Equal(3, args.LongOrNull("limit"));
    }

    [Fact]
    public void Boolean_string_is_coerced()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"literal\":{\"type\":\"boolean\"}},\"required\":[]}");
        var args = ToolArguments.Bind("x", doc.RootElement.Clone(), Obj("{\"literal\":\"true\"}"));
        Assert.True(args.BoolOrNull("literal"));
    }

    [Fact]
    public void Null_is_treated_as_missing()
    {
        var schema = SchemaOf("edit");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("edit", schema, Obj("{\"path\":null,\"old_string\":\"a\",\"new_string\":\"b\"}")));
        Assert.Contains("缺失必填参数", ex.Message);
        var readSchema = SchemaOf("read_file");
        var args = ToolArguments.Bind("read_file", readSchema, Obj("{\"path\":\"a.txt\",\"limit\":null}"));
        Assert.False(args.Has("limit"));
    }

    [Fact]
    public void Single_value_coerced_to_array()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"tags\":{\"type\":\"array\"}},\"required\":[]}");
        var args = ToolArguments.Bind("x", doc.RootElement.Clone(), Obj("{\"tags\":\"a\"}"));
        Assert.True(args.Has("tags"));
    }

    [Fact]
    public void Enum_violation_lists_allowed()
    {
        var schema = SchemaOf("memory_read");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("memory_read", schema, Obj("{\"scope\":\"foo\"}")));
        Assert.Contains("期望其中之一", ex.Message);
        Assert.Contains("\"global\"", ex.Message);
    }

    [Fact]
    public void Unknown_field_is_ignored()
    {
        var schema = SchemaOf("read_file");
        var args = ToolArguments.Bind("read_file", schema, Obj("{\"path\":\"a.txt\",\"zzz_unknown\":123}"));
        Assert.Equal("a.txt", args.StringOrNull("path"));
    }

    [Fact]
    public void Received_arguments_are_original_not_coerced()
    {
        var schema = SchemaOf("read_file");
        using var doc = JsonDocument.Parse("{\"path\":\"a.txt\",\"limit\":\"3\",\"offset\":1.5}");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("read_file", schema, doc.RootElement.Clone()));
        Assert.Contains("\"limit\":\"3\"", ex.Message.Replace(" ", ""));
    }

    [Fact]
    public void Multiple_errors_are_listed_in_schema_order()
    {
        var schema = SchemaOf("edit");
        var ex = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("edit", schema, Obj("{}")));
        var msg = ex.Message;
        var iPath = msg.IndexOf("path:", StringComparison.Ordinal);
        var iOld = msg.IndexOf("old_string:", StringComparison.Ordinal);
        var iNew = msg.IndexOf("new_string:", StringComparison.Ordinal);
        Assert.True(iPath >= 0 && iOld > iPath && iNew > iOld);
    }
}
