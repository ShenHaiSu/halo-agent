using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tests.TestSupport;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>MessageSequenceValidator 自身的反例单测：喂入故意非法的序列，必须能检出。</summary>
public sealed class MessageSequenceValidatorTests
{
    private static ChatMessage Asst(params string[] ids) => new()
    {
        RoleWire = "assistant",
        Content = null,
        ToolCalls = ids.Select(i => new ToolCall
        {
            Id = i,
            Function = new FunctionCall { Name = "list_directory", Arguments = "{}" },
        }).ToList(),
    };

    private static ChatMessage Tool(string id) =>
        new() { RoleWire = "tool", Content = "ok", ToolCallId = id };

    [Fact]
    public void Orphan_tool_message_is_detected()
    {
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            Tool("ghost"),
        };
        Assert.NotEmpty(MessageSequenceValidator.Validate(input));
    }

    [Fact]
    public void User_followed_by_tool_is_detected()
    {
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            Tool("c1"),
        };
        var violations = MessageSequenceValidator.Validate(input);
        Assert.Contains(violations, v => v.Contains("user 之后紧跟 tool"));
    }

    [Fact]
    public void Empty_tool_call_id_is_detected()
    {
        var bad = Asst(string.Empty);
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            bad,
            Tool(string.Empty),
        };
        Assert.Contains(
            MessageSequenceValidator.Validate(input),
            v => v.Contains("tool_call.id 为空"));
    }

    [Fact]
    public void Empty_function_name_is_detected()
    {
        var bad = new ChatMessage
        {
            RoleWire = "assistant",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "c1", Function = new FunctionCall { Name = string.Empty, Arguments = "{}" } },
            },
        };
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            bad,
            Tool("c1"),
        };
        Assert.Contains(
            MessageSequenceValidator.Validate(input),
            v => v.Contains("function.name 为空"));
    }

    [Fact]
    public void Unresponded_tool_call_at_end_is_detected()
    {
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            Asst("c1"),
        };
        Assert.Contains(
            MessageSequenceValidator.Validate(input),
            v => v.Contains("未响应"));
    }

    [Fact]
    public void Missing_arguments_field_is_detected()
    {
        var bad = new ChatMessage
        {
            RoleWire = "assistant",
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "c1", Function = new FunctionCall { Name = "x", Arguments = null! } },
            },
        };
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            bad,
            Tool("c1"),
        };
        Assert.Contains(
            MessageSequenceValidator.Validate(input),
            v => v.Contains("arguments 缺失"));
    }

    [Fact]
    public void Valid_sequence_passes()
    {
        var input = new List<ChatMessage>
        {
            new() { RoleWire = "user", Content = "go" },
            Asst("c1"),
            Tool("c1"),
            new() { RoleWire = "assistant", Content = "done" },
        };
        Assert.Empty(MessageSequenceValidator.Validate(input));
    }
}
