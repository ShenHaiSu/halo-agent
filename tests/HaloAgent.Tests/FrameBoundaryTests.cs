using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine;
using Host.Desktop.Bridge;
using Host.Desktop.Engine;
using Xunit;
using Xunit.Abstractions;

namespace HaloAgent.Tests;

/// <summary>
/// Host frame boundary: cutover switches default to the legacy chain, the
/// ring keeps a bounded ordered copy with gap reporting, and the converged
/// entry speaks JSON strings only while isolating malformed envelopes.
/// </summary>
public sealed class FrameBoundaryTests
{
    private readonly ITestOutputHelper _output;

    public FrameBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static HostBridge CreateBridge(string[]? batches, out InProcChannel channel)
    {
        var server = new FrameServer();
        var ring = new EventRingBuffer();
        channel = new InProcChannel(server, ring);
        return new HostBridge(channel, new EngineRouting(batches));
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    [Fact]
    public void RoutingDefaultsToLegacyChain()
    {
        var routing = new EngineRouting();

        Assert.False(routing.RoutesToEngine("session.new"));
        Assert.False(routing.RoutesToEngine("agent.prompt"));
        Assert.False(routing.RoutesToEngine("system.ping"));
        Assert.False(routing.RoutesToEngine("usage.query"));
        Assert.False(routing.RoutesToEngine("asset.skills.list"));
        Assert.False(routing.RoutesToEngine("no.such.method"));
        Assert.False(routing.RoutesToEngine(string.Empty));
    }

    [Fact]
    public void RoutingKeepsHostCapabilitiesOnShell()
    {
        var routing = new EngineRouting(new[] { "A", "B", "C" });

        Assert.True(routing.IsHostOnly("host.pickDirectory"));
        Assert.True(routing.IsHostOnly("host.openPath"));
        Assert.False(routing.RoutesToEngine("host.pickDirectory"));
        Assert.False(routing.RoutesToEngine("host.openPath"));
    }

    [Fact]
    public void RoutingEnablesEachBatchIndependently()
    {
        var sessionTurns = new EngineRouting(new[] { "A" });
        Assert.True(sessionTurns.RoutesToEngine("session.new"));
        Assert.True(sessionTurns.RoutesToEngine("agent.prompt"));
        Assert.True(sessionTurns.RoutesToEngine("state.snapshot"));
        Assert.True(sessionTurns.RoutesToEngine("tool.undoRollback"));
        Assert.False(sessionTurns.RoutesToEngine("system.ping"));
        Assert.False(sessionTurns.RoutesToEngine("usage.query"));
        Assert.False(sessionTurns.RoutesToEngine("asset.skills.list"));

        var configUsage = new EngineRouting(new[] { "B" });
        Assert.True(configUsage.RoutesToEngine("system.ping"));
        Assert.True(configUsage.RoutesToEngine("settings.saveProviders"));
        Assert.True(configUsage.RoutesToEngine("usage.query"));
        Assert.False(configUsage.RoutesToEngine("session.new"));
        Assert.False(configUsage.RoutesToEngine("asset.memory.save"));

        var toolAssets = new EngineRouting(new[] { "C" });
        Assert.True(toolAssets.RoutesToEngine("asset.skills.list"));
        Assert.True(toolAssets.RoutesToEngine("asset.memory.save"));
        Assert.False(toolAssets.RoutesToEngine("session.list"));
        Assert.False(toolAssets.RoutesToEngine("system.info"));
    }

    [Fact]
    public void RingKeepsOrderAndCapsSingleReads()
    {
        var ring = new EventRingBuffer();
        for (int i = 0; i < 600; i++)
        {
            ring.Append("text.delta", new { index = i });
        }

        RingBatch batch = ring.Read(0, 10_000);

        Assert.Equal(500, batch.Frames.Count);
        Assert.False(batch.HasGap);
        long[] ordered = batch.Frames.Select(f => f.Sequence).ToArray();
        Assert.Equal(ordered.OrderBy(v => v), ordered);
        Assert.Equal(500, batch.NextSequence);
    }

    [Fact]
    public void RingReportsGapAfterOverflow()
    {
        var ring = new EventRingBuffer(capacity: 8);
        for (int i = 0; i < 20; i++)
        {
            ring.Append("text.delta", new { index = i });
        }

        RingBatch batch = ring.Read(0, 500);

        Assert.True(batch.HasGap);
        Assert.True(batch.DroppedEstimate > 0);
        Assert.Equal(8, batch.Frames.Count);
    }

    [Fact]
    public void RingIgnoresStaleDuplicates()
    {
        var ring = new EventRingBuffer();
        ring.Append("turn.started", new { turnId = "t1" });
        long latest = ring.LatestSequence;

        using var document = JsonDocument.Parse("{}");
        ring.Append(
            new HaloAgent.Engine.Abstractions.EventFrame(
                latest,
                "turn.started",
                document.RootElement
            )
        );

        Assert.Equal(1, ring.RetainedCount);
    }

    [Fact]
    public async Task InvokePingRoundTripThroughBridge()
    {
        HostBridge bridge = CreateBridge(new[] { "B" }, out InProcChannel channel);
        await using var lifetime = channel;

        string raw = await bridge.invoke("{\"t\":\"req\",\"id\":1,\"m\":\"system.ping\",\"p\":{}}");

        using JsonDocument document = Parse(raw);
        JsonElement root = document.RootElement;
        Assert.Equal("res", root.GetProperty("t").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("r").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task InvokeSessionFlowSurfacesEventsInBatch()
    {
        HostBridge bridge = CreateBridge(new[] { "A" }, out InProcChannel channel);
        await using var lifetime = channel;

        string created = await bridge.invoke(
            "{\"t\":\"req\",\"id\":10,\"m\":\"session.new\",\"p\":{\"title\":\"demo\"}}"
        );
        using (JsonDocument createdDocument = Parse(created))
        {
            Assert.True(createdDocument.RootElement.GetProperty("ok").GetBoolean());
        }

        string batch = await bridge.getEventBatch(0, 500);
        using JsonDocument batchDocument = Parse(batch);
        JsonElement root = batchDocument.RootElement;
        Assert.False(root.GetProperty("hasGap").GetBoolean());
        string[] kinds = root.GetProperty("frames")
            .EnumerateArray()
            .Select(f => f.GetProperty("k").GetString()!)
            .ToArray();
        Assert.Contains("session.updated", kinds);
        Assert.True(root.GetProperty("nextSeq").GetInt64() > 0);
    }

    [Fact]
    public async Task InvokeLegacyMethodExplainsRollbackPath()
    {
        HostBridge bridge = CreateBridge(null, out InProcChannel channel);
        await using var lifetime = channel;

        string raw = await bridge.invoke("{\"t\":\"req\",\"id\":3,\"m\":\"session.new\",\"p\":{}}");

        using JsonDocument document = Parse(raw);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("NOT_FOUND", root.GetProperty("e").GetProperty("code").GetString());
        Assert.True(root.GetProperty("e").TryGetProperty("hint", out _));
    }

    [Fact]
    public async Task InvokeMalformedEnvelopeNeverBreaksLaterCalls()
    {
        HostBridge bridge = CreateBridge(new[] { "B" }, out InProcChannel channel);
        await using var lifetime = channel;

        foreach (string bad in new[] { string.Empty, "not-json", "[1,2]", "{\"t\":\"req\"}" })
        {
            string raw = await bridge.invoke(bad);
            using JsonDocument document = Parse(raw);
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "INVALID_PARAMS",
                document.RootElement.GetProperty("e").GetProperty("code").GetString()
            );
        }

        string good = await bridge.invoke(
            "{\"t\":\"req\",\"id\":9,\"m\":\"system.ping\",\"p\":{}}"
        );
        using JsonDocument goodDocument = Parse(good);
        Assert.True(goodDocument.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task InvokeUnknownMethodReportsNotFound()
    {
        HostBridge bridge = CreateBridge(new[] { "A", "B", "C" }, out InProcChannel channel);
        await using var lifetime = channel;

        string raw = await bridge.invoke(
            "{\"t\":\"req\",\"id\":4,\"m\":\"session.missing\",\"p\":{}}"
        );

        using JsonDocument document = Parse(raw);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "NOT_FOUND",
            document.RootElement.GetProperty("e").GetProperty("code").GetString()
        );
    }

    [Fact]
    public async Task InvokeCancelWithoutTurnReportsUsableError()
    {
        HostBridge bridge = CreateBridge(new[] { "A" }, out InProcChannel channel);
        await using var lifetime = channel;

        string raw = await bridge.invoke(
            "{\"t\":\"req\",\"id\":5,\"m\":\"agent.cancel\",\"p\":{\"turnId\":\"missing\"}}"
        );

        using JsonDocument document = Parse(raw);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        string code = root.GetProperty("e").GetProperty("code").GetString()!;
        Assert.True(code == "NOT_FOUND" || code == "INVALID_PARAMS", $"unexpected code {code}");
    }

    [Fact]
    public async Task HostPickDirectoryWithoutFormCancelsCleanly()
    {
        HostBridge bridge = CreateBridge(null, out InProcChannel channel);
        await using var lifetime = channel;

        string raw = await bridge.invoke(
            "{\"t\":\"req\",\"id\":6,\"m\":\"host.pickDirectory\",\"p\":{}}"
        );

        using JsonDocument document = Parse(raw);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BatchedReadOfFullBatchStaysInteractive()
    {
        HostBridge bridge = CreateBridge(new[] { "B" }, out InProcChannel channel);
        await using var lifetime = channel;

        for (int i = 0; i < 500; i++)
        {
            channel.Ring.Append("text.delta", new { index = i, pad = new string('x', 64) });
        }

        // Warm up the serializer, then time a sustained burst of full reads.
        _ = await bridge.getEventBatch(0, 500);
        var timer = Stopwatch.StartNew();
        const int burst = 120;
        for (int i = 0; i < burst; i++)
        {
            _ = await bridge.getEventBatch(0, 500);
        }
        timer.Stop();
        double average = timer.Elapsed.TotalMilliseconds / burst;

        string raw = await bridge.getEventBatch(0, 500);
        using JsonDocument document = Parse(raw);
        Assert.Equal(500, document.RootElement.GetProperty("frames").GetArrayLength());
        _output.WriteLine(
            $"full-batch read: burst={burst} average={average:F3}ms total={timer.Elapsed.TotalMilliseconds:F1}ms"
        );
        Assert.True(
            average < 8,
            $"full batch average {average:F3}ms exceeds the interactivity budget"
        );
    }
}
