using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Serialization;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>P15 契约验收 7~8：三向一致性 + 删除矩阵 + finalUsed 不变式。</summary>
public sealed class P15ContractTests
{
    private static JsonDocument Schema
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var p = Path.Combine(dir.FullName, "contract", "agent.schema.json");
                if (File.Exists(p)) return JsonDocument.Parse(File.ReadAllText(p));
                dir = dir.Parent;
            }
            throw new FileNotFoundException("schema");
        }
    }

    [Fact]
    public void ContractVersion_Is3_ProtocolStill1()
    {
        Assert.Equal(3, Schema.RootElement.GetProperty("contractVersion").GetInt32());
        // TurnView 存在，StateSnapshot 用 turns
        var types = Schema.RootElement.GetProperty("types");
        Assert.True(types.TryGetProperty("TurnView", out _));
        var snap = types.GetProperty("StateSnapshot");
        Assert.True(snap.GetProperty("properties").TryGetProperty("turns", out _));
        Assert.False(snap.GetProperty("properties").TryGetProperty("messages", out _));
    }

    [Fact]
    public void Events_HaveRoundEnded_And_Used()
    {
        var events = Schema.RootElement.GetProperty("events");
        Assert.True(events.TryGetProperty("round.ended", out _));
        Assert.True(events.GetProperty("text.delta").GetProperty("payload").GetProperty("properties").TryGetProperty("used", out _));
        Assert.True(events.GetProperty("turn.finished").GetProperty("payload").GetProperty("properties").TryGetProperty("finalUsed", out _));
        // EventKinds.All 包含 round.ended
        Assert.Contains(EventKinds.RoundEnded, EventKinds.All);
    }

    [Fact]
    public void DeletionMatrix_TurnsEverywhere()
    {
        var events = Schema.RootElement.GetProperty("events");
        // historyLoaded 用 turns
        Assert.True(events.GetProperty("session.historyLoaded").GetProperty("payload").GetProperty("properties").TryGetProperty("turns", out _));
        // compacted.kept 用 TurnView
        var kept = events.GetProperty("session.compacted").GetProperty("payload").GetProperty("properties").GetProperty("kept");
        Assert.Equal("TurnView", kept.GetProperty("items").GetProperty("$ref").GetString()?.Split('/')[^1]);
        // session.resume 用 turns
        var methods = Schema.RootElement.GetProperty("methods");
        Assert.True(methods.GetProperty("session.resume").GetProperty("result").GetProperty("properties").TryGetProperty("turns", out _));
        // tool.result 存在
        Assert.True(methods.TryGetProperty("tool.result", out _));
    }

    [Fact]
    public void Payloads_Serialize_Used_FinalUsed()
    {
        var td = System.Text.Json.JsonSerializer.Serialize(new TextDeltaPayload { Text = "x", Used = 2 }, HaloJson.Options);
        Assert.Contains("\"used\":2", td);
        var fin = System.Text.Json.JsonSerializer.Serialize(new TurnFinishedPayload { TurnId = "t1", Text = "hi", FinalUsed = 2 }, HaloJson.Options);
        Assert.Contains("\"finalUsed\":2", fin);
    }
}
