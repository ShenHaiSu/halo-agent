using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Cutover rollout summary: the default probe serves the frame chain, every
/// catalogued failure code flows through the shared fail entry, the journal
/// keeps its ring bounds, sessions stay workspace-scoped, and failed turns
/// settle through the same finish entry as completed ones.
/// </summary>
public sealed class CutoverSummaryTests
{
    private const string RoutingVariable = "HALO_ENGINE_ROUTING";

    private static string? ClearRoutingVariable()
    {
        string? previous;
        try
        {
            previous = Environment.GetEnvironmentVariable(RoutingVariable);
            Environment.SetEnvironmentVariable(RoutingVariable, null);
        }
        catch
        {
            previous = null;
        }
        return previous;
    }

    private static void RestoreRoutingVariable(string? previous)
    {
        try
        {
            Environment.SetEnvironmentVariable(RoutingVariable, previous);
        }
        catch { }
    }

    [Fact]
    public async Task DefaultProbeServesFrameChainWithVersions()
    {
        string? previous = ClearRoutingVariable();
        try
        {
            await using var server = new FrameServer();
            var info = await server.SubmitAsync(RequestFrame.WithPayload(9101, "system.info"));
            Assert.True(info.Ok);
            var root = info.Result!.Value;
            Assert.Equal(
                ContractCatalog.ContractVersion,
                root.GetProperty("contractVersion").GetString()
            );
            Assert.Equal(
                ContractCatalog.BridgeProtocolVersion,
                root.GetProperty("bridgeVersion").GetInt32()
            );
            var routing = root.GetProperty("routing");
            Assert.True(routing.GetProperty("defaultFrame").GetBoolean());
            string[] batches = routing
                .GetProperty("enabledBatches")
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .OrderBy(b => b, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "A", "B", "C" }, batches);
        }
        finally
        {
            RestoreRoutingVariable(previous);
        }
    }

    [Fact]
    public void ErrorCatalogKeepsElevenCodesThroughFailEntry()
    {
        string[] expected =
        {
            "INVALID_PARAMS",
            "NOT_FOUND",
            "TURN_IN_PROGRESS",
            "COMPACTING",
            "RATE_LIMITED",
            "PROVIDER_AUTH",
            "NETWORK_FAIL",
            "TOOL_DENIED",
            "TOOL_TIMEOUT",
            "CANCELLED",
            "INTERNAL",
        };
        Assert.Equal(
            expected.OrderBy(c => c, StringComparer.Ordinal),
            ContractCatalog.ErrorCodes.OrderBy(c => c, StringComparer.Ordinal)
        );
        foreach (string code in ContractCatalog.ErrorCodes)
        {
            var failed = ResponseFrame.Fail(9201, code, "cutover emission probe");
            Assert.False(failed.Ok);
            Assert.Equal(code, failed.Error!.Code);
        }
    }

    [Fact]
    public void JournalKeepsRingBoundsWithHeadDrop()
    {
        Assert.Equal(4096, MemoryEventJournal.DefaultCapacity);
        Assert.Equal(500, MemoryEventJournal.MaxBatch);
        var journal = new MemoryEventJournal();
        for (int i = 0; i < MemoryEventJournal.DefaultCapacity + 10; i++)
        {
            journal.Append("text.delta", new { index = i });
        }
        var batch = journal.ReadSince(0, 10_000);
        Assert.Equal(MemoryEventJournal.MaxBatch, batch.Frames.Count);
        Assert.True(batch.HasGap);
        Assert.True(batch.DroppedEstimate > 0);
    }

    [Fact]
    public async Task SessionsStayWorkspaceIsolated()
    {
        await using var server = new FrameServer();
        var first = await server.SubmitAsync(
            RequestFrame.WithPayload(
                9301,
                "session.new",
                new { title = "ws-a-one", workspaceId = "ws-a" }
            )
        );
        Assert.True(first.Ok);
        var second = await server.SubmitAsync(
            RequestFrame.WithPayload(
                9302,
                "session.new",
                new { title = "ws-b-one", workspaceId = "ws-b" }
            )
        );
        Assert.True(second.Ok);

        var listedA = await server.SubmitAsync(
            RequestFrame.WithPayload(9303, "session.list", new { workspaceId = "ws-a" })
        );
        Assert.True(listedA.Ok);
        string[] titlesA = listedA
            .Result!.Value.GetProperty("sessions")
            .EnumerateArray()
            .Select(e => e.GetProperty("title").GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("ws-a-one", titlesA);
        Assert.DoesNotContain("ws-b-one", titlesA);
    }

    [Fact]
    public void FailedTurnsSettleThroughFinishEntry()
    {
        var turns = new TurnCoordinator(new EngineState());
        Assert.True(turns.TryStart("sess-cutover", "turn-cutover-1", 9401, out _));
        Assert.True(turns.IsActive("turn-cutover-1"));
        turns.Finish("turn-cutover-1", TurnOutcome.Failed);
        Assert.False(turns.IsActive("turn-cutover-1"));
        var failed = ResponseFrame.Fail(9402, "INTERNAL", "turn failed");
        Assert.False(failed.Ok);
        Assert.Equal("INTERNAL", failed.Error!.Code);
    }

    [Fact]
    public async Task UnknownSessionPromptFailsClosed()
    {
        await using var server = new FrameServer();
        var reply = await server.SubmitAsync(
            RequestFrame.WithPayload(
                9501,
                "agent.prompt",
                new { sessionId = "missing-session", text = "hi" }
            )
        );
        Assert.False(reply.Ok);
        Assert.Equal("NOT_FOUND", reply.Error!.Code);
    }
}
