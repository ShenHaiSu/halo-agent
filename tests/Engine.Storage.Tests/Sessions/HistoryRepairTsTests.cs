using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Sessions;

/// <summary>P14-4 Repair_Preserves_Ts：占位 ts = parent+1，孤儿丢弃不留洞。</summary>
public sealed class HistoryRepairTsTests
{
    private static StoredMessage AssistantWithCalls(long tsMs, params string[] ids) =>
        StoredMessage.AssistantWithToolCalls(null,
            ids.Select(id => new StoredToolCall
            {
                Id = id,
                Function = new StoredFunctionCall { Name = "read_file", Arguments = "{}" },
            }).ToList(),
            tsMs);

    [Fact]
    public void Placeholder_Ts_Is_Parent_Plus_One()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q", tsMs: 4999),
            AssistantWithCalls(5000, "c1", "c2"),
            StoredMessage.User("follow-up", tsMs: 6000),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(2, repaired);
        Assert.Equal(5, list.Count);
        Assert.Equal("c1", list[2].ToolCallId);
        Assert.Equal("c2", list[3].ToolCallId);
        Assert.Equal(5001, list[2].TsMs);
        Assert.Equal(5002, list[3].TsMs);
        // 单调：parent < 占位 < 后续
        Assert.True(list[1].TsMs < list[2].TsMs);
        Assert.True(list[2].TsMs <= list[3].TsMs);
        Assert.True(list[3].TsMs < list[4].TsMs);
    }

    [Fact]
    public void Placeholder_Ts_Zero_When_Parent_Unknown()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q"),
            AssistantWithCalls(0, "c1"),
            StoredMessage.User("next"),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(1, repaired);
        Assert.Equal(0, list[2].TsMs);
    }

    [Fact]
    public void Orphan_Tool_Dropped_Without_Hole()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q", tsMs: 1000),
            StoredMessage.Tool("ghost", "x", tsMs: 1001),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(0, repaired);
        Assert.Single(list);
        Assert.Equal(StoredRole.User, list[0].Role);
    }
}
