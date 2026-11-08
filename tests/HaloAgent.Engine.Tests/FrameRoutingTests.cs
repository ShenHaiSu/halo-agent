using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Skeleton routing: known methods answer, unknown methods report NOT_FOUND,
/// malformed calls report INVALID_PARAMS without breaking the read loop.
/// </summary>
public sealed class FrameRoutingTests
{
    [Fact]
    public async Task PingRoundTrip()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(RequestFrame.WithPayload(1, "system.ping"));
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task UnknownMethodReportsNotFound()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(RequestFrame.WithPayload(2, "session.missing"));
        Assert.False(response.Ok);
        Assert.Equal("NOT_FOUND", response.Error!.Code);
    }

    [Fact]
    public async Task EmptyMethodReportsInvalidParams()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(RequestFrame.WithPayload(3, string.Empty));
        Assert.False(response.Ok);
        Assert.Equal("INVALID_PARAMS", response.Error!.Code);
    }

    [Fact]
    public async Task BadFrameDoesNotBreakLaterFrames()
    {
        await using var server = new FrameServer();
        var bad = await server.SubmitAsync(RequestFrame.WithPayload(4, string.Empty));
        Assert.False(bad.Ok);
        var good = await server.SubmitAsync(RequestFrame.WithPayload(5, "system.ping"));
        Assert.True(good.Ok);
    }

    [Fact]
    public async Task SameRequestIdReplaysStoredReply()
    {
        await using var server = new FrameServer();
        var first = await server.SubmitAsync(RequestFrame.WithPayload(6, "system.ping"));
        var second = await server.SubmitAsync(RequestFrame.WithPayload(6, "system.ping"));
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Result?.ToString(), second.Result?.ToString());
    }

    [Fact]
    public async Task SessionNewThenPromptEmitsOrderedEvents()
    {
        await using var server = new FrameServer();
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(10, "session.new", new { title = "demo" })
        );
        Assert.True(created.Ok);

        string sessionId = created.Result!.Value.GetProperty("sessionId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var answered = await server.SubmitAsync(
            RequestFrame.WithPayload(11, "agent.prompt", new { sessionId, text = "hello" })
        );
        Assert.True(answered.Ok);

        var batch = server.ReadEvents(0, 500);
        Assert.False(batch.HasGap);
        string[] kinds = batch.Frames.Select(f => f.Kind).ToArray();
        Assert.Contains("turn.started", kinds);
        Assert.Contains("turn.done", kinds);
        long[] ordered = batch.Frames.Select(f => f.Sequence).ToArray();
        Assert.Equal(ordered.OrderBy(v => v), ordered);
    }
}
