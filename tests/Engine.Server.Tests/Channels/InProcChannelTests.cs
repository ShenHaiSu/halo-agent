using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Channels;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Channels;

public sealed class InProcChannelTests : FrameChannelContractTests
{
    protected override Task<(IFrameChannel a, IFrameChannel b)> CreatePairAsync()
    {
        var (a, b) = InProcChannel.CreatePair();
        return Task.FromResult<(IFrameChannel, IFrameChannel)>((a, b));
    }

    /// <summary>
    /// InProc 不做序列化，所以必须额外验证"帧经过 JSON 往返后仍然等价"。
    /// 否则 InProc 测试全绿但生产（走 pipe 序列化）挂掉。
    /// </summary>
    [Fact]
    public void Frame_survives_json_roundtrip()
    {
        var original = new EventFrame
        {
            Seq = 42,
            Kind = "tool.started",
            Payload = JsonSerializer.SerializeToElement(
                new { toolCallId = "c1", name = "read_file", args = "{\"path\":\"a.txt\"}" },
                HaloAgent.Engine.Abstractions.Serialization.HaloJson.Options),
        };

        var bytes = FrameCodec.ToBytes(original);
        var parsed = FrameCodec.Parse(bytes.AsSpan(0, bytes.Length - 1), out var err);

        Assert.Null(err);
        var ev = Assert.IsType<EventFrame>(parsed);
        Assert.Equal(42, ev.Seq);
        Assert.Equal("tool.started", ev.Kind);
        Assert.Contains("read_file", ev.Payload!.Value.GetRawText());
    }
}
