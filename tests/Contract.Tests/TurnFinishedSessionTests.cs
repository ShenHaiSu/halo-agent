using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Serialization;
using Xunit;

namespace HaloAgent.Contract.Tests;

public sealed class TurnFinishedSessionTests
{
    [Fact]
    public void TurnFinished_payload_session_roundtrip()
    {
        var payload = new TurnFinishedPayload
        {
            TurnId = "t-1",
            Text = "hi",
            FinalUsed = 1,
            Session = new TurnFinishedSessionSnapshot
            {
                Id = "s-1",
                Title = "title",
                MtimeMs = 7,
                UserMsgs = 1,
                AssistantMsgs = 1,
            },
        };
        var el = JsonSerializer.SerializeToElement(payload, HaloJson.Options);
        Assert.True(el.TryGetProperty("session", out var session));
        Assert.Equal("s-1", session.GetProperty("id").GetString());
        Assert.Equal("title", session.GetProperty("title").GetString());

        var plain = JsonSerializer.SerializeToElement(
            new TurnFinishedPayload { TurnId = "t-2", Text = "hi", FinalUsed = 1 },
            HaloJson.Options);
        Assert.False(plain.TryGetProperty("session", out _));
    }
}
