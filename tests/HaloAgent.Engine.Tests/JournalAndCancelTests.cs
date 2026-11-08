using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Journal bounds and cancel path: batches cap at 500, overflow reports a gap,
/// and cancel signals the turn token without taking the frame write gate.
/// </summary>
public sealed class JournalAndCancelTests
{
    [Fact]
    public void BatchSizeNeverExceedsFiveHundred()
    {
        var journal = new MemoryEventJournal();
        for (int i = 0; i < 600; i++)
        {
            journal.Append("usage.tick", new { index = i });
        }

        var batch = journal.ReadSince(0, 900);
        Assert.Equal(500, batch.Frames.Count);
        Assert.False(batch.HasGap);
    }

    [Fact]
    public void OverflowDropsOldestAndReportsGap()
    {
        var journal = new MemoryEventJournal(capacity: 8);
        for (int i = 0; i < 12; i++)
        {
            journal.Append("usage.tick", new { index = i });
        }

        var batch = journal.ReadSince(0, 500);
        Assert.True(batch.HasGap);
        Assert.True(batch.DroppedEstimate > 0);
        Assert.Equal(8, batch.Frames.Count);
    }

    [Fact]
    public async Task CancelSignalsTurnWithoutWriteGate()
    {
        await using var server = new FrameServer();
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(20, "session.new", new { title = "demo" })
        );
        string sessionId = created.Result!.Value.GetProperty("sessionId").GetString()!;
        var prompted = await server.SubmitAsync(
            RequestFrame.WithPayload(21, "agent.prompt", new { sessionId, text = "hi" })
        );
        string turnId = prompted.Result!.Value.GetProperty("turnId").GetString()!;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await server.SubmitAsync(
            RequestFrame.WithPayload(22, "agent.cancel", new { turnId })
        );
        watch.Stop();

        Assert.True(cancelled.Ok);
        Assert.True(watch.ElapsedMilliseconds < 1000);
        Assert.True(server.State.TurnToken(turnId).IsCancellationRequested);
    }

    [Fact]
    public async Task MissingTurnIdReportsInvalidParams()
    {
        await using var server = new FrameServer();
        var response = await server.SubmitAsync(
            RequestFrame.WithPayload(23, "agent.cancel", new { })
        );
        Assert.False(response.Ok);
        Assert.Equal("INVALID_PARAMS", response.Error!.Code);
    }
}
