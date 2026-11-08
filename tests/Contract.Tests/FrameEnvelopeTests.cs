using HaloAgent.Bridge.Contracts;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// Frame-envelope behavior: well-formed request/response/event frames classify
/// cleanly, malformed bytes and missing required fields are reported as bad
/// frames (caller skips them and keeps the connection alive), and surplus
/// fields are accepted for forward compatibility.
/// </summary>
public sealed class FrameEnvelopeTests
{
    [Theory]
    [InlineData("{\"t\":\"req\",\"id\":42,\"m\":\"agent.prompt\",\"p\":{\"text\":\"hi\"}}", "req")]
    [InlineData("{\"t\":\"res\",\"id\":42,\"ok\":true,\"r\":{}}", "res")]
    [InlineData(
        "{\"t\":\"res\",\"id\":43,\"ok\":false,\"e\":{\"code\":\"TURN_IN_PROGRESS\",\"msg\":\"busy\"}}",
        "res"
    )]
    [InlineData("{\"t\":\"evt\",\"seq\":10031,\"k\":\"text.delta\",\"p\":{}}", "evt")]
    public void WellFormedFrames_Classify(string json, string expectedKind)
    {
        Assert.True(AgentContract.TryClassifyFrame(json, out string kind));
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json-at-all")]
    [InlineData("{\"t\":\"req\",\"id\":42")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("{\"t\":\"unknown\",\"id\":1}")]
    public void MalformedFrames_ReportBadFrame(string? json)
    {
        Assert.False(AgentContract.TryClassifyFrame(json, out _));
    }

    [Theory]
    [InlineData("{\"t\":\"req\",\"m\":\"agent.prompt\"}")]
    [InlineData("{\"t\":\"req\",\"id\":1}")]
    [InlineData("{\"t\":\"res\",\"id\":1}")]
    [InlineData("{\"t\":\"res\",\"ok\":true}")]
    [InlineData("{\"t\":\"evt\",\"k\":\"text.delta\"}")]
    [InlineData("{\"t\":\"evt\",\"seq\":7}")]
    [InlineData("{\"id\":1}")]
    public void MissingRequiredFields_ReportBadFrame(string json)
    {
        Assert.False(AgentContract.TryClassifyFrame(json, out _));
    }

    [Theory]
    [InlineData(
        "{\"t\":\"req\",\"id\":7,\"m\":\"agent.prompt\",\"futureOptional\":\"kept\"}",
        "req"
    )]
    [InlineData("{\"t\":\"res\",\"id\":7,\"ok\":true,\"r\":{},\"traceId\":\"abc\"}", "res")]
    [InlineData("{\"t\":\"evt\",\"seq\":7,\"k\":\"usage.tick\",\"note\":\"extra\"}", "evt")]
    public void SurplusFields_AreAccepted(string json, string expectedKind)
    {
        Assert.True(AgentContract.TryClassifyFrame(json, out string kind));
        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void BadFrame_DoesNotBreakSubsequentFrames()
    {
        string[] inbound =
        {
            "{\"t\":\"evt\",\"seq\":1,\"k\":\"turn.started\"}",
            "broken-bytes",
            "{\"t\":\"evt\",\"seq\":2,\"k\":\"turn.done\"}",
        };

        var accepted = new List<string>();
        foreach (string line in inbound)
        {
            if (AgentContract.TryClassifyFrame(line, out string kind))
            {
                accepted.Add(kind);
            }
        }

        Assert.Equal(new[] { "evt", "evt" }, accepted);
    }
}
