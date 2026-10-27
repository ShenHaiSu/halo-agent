using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Sessions;

public class HistoryRepairTests
{
    private static StoredMessage Assistant2Calls(string id1, string id2) =>
        StoredMessage.AssistantWithToolCalls(null, new List<StoredToolCall>
        {
            new StoredToolCall { Id = id1, Function = new StoredFunctionCall { Name = "read_file", Arguments = "{}" } },
            new StoredToolCall { Id = id2, Function = new StoredFunctionCall { Name = "glob", Arguments = "{}" } },
        });

    [Fact]
    public void Orphan_calls_then_user_message_get_two_placeholders()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q"),
            Assistant2Calls("c1", "c2"),
            StoredMessage.User("follow-up"),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(2, repaired);
        Assert.Equal(5, list.Count);
        Assert.Equal(StoredRole.Tool, list[2].Role);
        Assert.Equal("c1", list[2].ToolCallId);
        Assert.Equal("c2", list[3].ToolCallId);
        Assert.Equal(HistoryRepair.PlaceholderToolReply, list[2].Content);
    }

    [Fact]
    public void Trailing_orphan_calls_are_appended_at_end()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q"),
            Assistant2Calls("c1", "c2"),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(2, repaired);
        Assert.Equal(4, list.Count);
        Assert.Equal("c1", list[2].ToolCallId);
        Assert.Equal("c2", list[3].ToolCallId);
    }

    [Fact]
    public void Orphan_tool_message_is_dropped()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.User("q"),
            StoredMessage.Tool("ghost", "x"),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(0, repaired);
        Assert.Single(list);
        Assert.Equal(StoredRole.User, list[0].Role);
    }

    [Fact]
    public void Legal_history_is_unchanged()
    {
        var list = new List<StoredMessage>
        {
            StoredMessage.AssistantWithToolCalls(null, new List<StoredToolCall>
            {
                new StoredToolCall { Id = "c1", Function = new StoredFunctionCall { Name = "read_file", Arguments = "{}" } },
            }),
            StoredMessage.Tool("c1", "content"),
            StoredMessage.User("thanks"),
        };

        var repaired = HistoryRepair.Repair(list);

        Assert.Equal(0, repaired);
        Assert.Equal(3, list.Count);
        Assert.Equal("c1", list[1].ToolCallId);
    }
}
