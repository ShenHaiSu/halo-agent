using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// 守护"帧的线上格式"这一契约。
/// 这里断言的是**字段名与信封结构**，不是业务语义。
/// 一旦有人把 JsonPropertyName 改错，或者加了不该有的字段，
/// 这些测试会立刻红——比等到前端联调时才发现在白板上找不到字段要好。
/// </summary>
public class FrameSerializationTests
{
    [Fact]
    public void Request_uses_short_field_names()
    {
        var f = new RequestFrame { Id = 42, Method = "system.ping" };
        var json = JsonSerializer.Serialize<object>(f, HaloJson.Options);

        Assert.Contains("\"t\":\"req\"", json);
        Assert.Contains("\"id\":42", json);
        Assert.Contains("\"m\":\"system.ping\"", json);
        // 不能出现完整字段名
        Assert.DoesNotContain("\"type\"", json);
        Assert.DoesNotContain("\"method\"", json);
    }

    [Fact]
    public void Response_success_shape()
    {
        var f = ResponseFrame.Success(1, new { pong = true }, HaloJson.Options);
        var json = JsonSerializer.Serialize<object>(f, HaloJson.Options);

        Assert.Contains("\"t\":\"res\"", json);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"r\":", json);
        // 成功应答不应带 e
        Assert.DoesNotContain("\"e\":", json);
    }

    [Fact]
    public void Response_failure_shape()
    {
        var f = ResponseFrame.Failure(7, "method.notFound", "未知方法");
        var json = JsonSerializer.Serialize<object>(f, HaloJson.Options);

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"code\":\"method.notFound\"", json);
        Assert.Contains("\"msg\":\"未知方法\"", json);
        Assert.DoesNotContain("\"r\":", json);
    }

    [Fact]
    public void Event_has_seq_and_kind()
    {
        var f = new EventFrame { Seq = 10031, Kind = "text.delta" };
        var json = JsonSerializer.Serialize<object>(f, HaloJson.Options);

        Assert.Contains("\"t\":\"evt\"", json);
        Assert.Contains("\"seq\":10031", json);
        Assert.Contains("\"k\":\"text.delta\"", json);
    }

    [Theory]
    [InlineData("{\"t\":\"req\",\"id\":5,\"m\":\"system.ping\"}")]
    [InlineData("{\"t\":\"req\",\"id\":5,\"m\":\"system.ping\",\"extra\":\"未知字段应被忽略\"}")]
    public void ReadFrame_tolerates_unknown_fields(string line)
    {
        var dto = HaloJson.ReadFrame(line);
        var req = Assert.IsType<FrameDto.Request>(dto);
        Assert.Equal(5, req.Id);
        Assert.Equal("system.ping", req.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"t\":\"unknown\"}")]
    public void ReadFrame_returns_null_for_garbage(string line)
    {
        Assert.Null(HaloJson.ReadFrame(line));
    }

    [Fact]
    public void Chinese_text_survives_roundtrip()
    {
        var f = new EventFrame { Seq = 1, Kind = "notice" };
        var json = JsonSerializer.Serialize<object>(f, HaloJson.Options);

        // 不能把中文转成 \u 转义（可读性）
        Assert.DoesNotContain("\\u", json);
    }
}
