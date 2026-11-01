using System.Reflection;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tests.TestSupport;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>
/// 回合级切分、动态上下文拼装与协议链重置单测。
/// </summary>
public sealed class TurnSegmenterTests
{
    [Fact]
    public void Compact_Segments_By_Turn_Integrity()
    {
        // 构造 5 个完整回合，其中第 4 回合包含 ToolCall 与 Tool 输出
        var messages = new List<ChatMessage>();

        // Turn 1
        messages.Add(new ChatMessage { RoleWire = "user", Content = "Q1", TurnId = "t-1", Ord = 0 });
        messages.Add(new ChatMessage { RoleWire = "assistant", Content = "A1", TurnId = "t-1", Ord = 1 });

        // Turn 2
        messages.Add(new ChatMessage { RoleWire = "user", Content = "Q2", TurnId = "t-2", Ord = 0 });
        messages.Add(new ChatMessage { RoleWire = "assistant", Content = "A2", TurnId = "t-2", Ord = 1 });

        // Turn 3
        messages.Add(new ChatMessage { RoleWire = "user", Content = "Q3", TurnId = "t-3", Ord = 0 });
        messages.Add(new ChatMessage { RoleWire = "assistant", Content = "A3", TurnId = "t-3", Ord = 1 });

        // Turn 4: 带 ToolCall 和 Tool 输出
        messages.Add(new ChatMessage { RoleWire = "user", Content = "Q4", TurnId = "t-4", Ord = 0 });
        messages.Add(new ChatMessage
        {
            RoleWire = "assistant",
            Content = null,
            TurnId = "t-4",
            Ord = 1,
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call-1", Function = new FunctionCall { Name = "read_file", Arguments = "{}" } }
            }
        });
        messages.Add(new ChatMessage
        {
            RoleWire = "tool",
            Content = "file content",
            ToolCallId = "call-1",
            TurnId = "t-4",
            Ord = 2
        });
        messages.Add(new ChatMessage { RoleWire = "assistant", Content = "A4 final", TurnId = "t-4", Ord = 3 });

        // Turn 5
        messages.Add(new ChatMessage { RoleWire = "user", Content = "Q5", TurnId = "t-5", Ord = 0 });
        messages.Add(new ChatMessage { RoleWire = "assistant", Content = "A5", TurnId = "t-5", Ord = 1 });

        // 执行切分，保留尾部 2 个完整回合
        var (oldTurns, keepTurns) = TurnSegmenter.Segment(messages, keepTailTurns: 2);

        // 前 3 个回合被送入 oldTurns
        Assert.Equal(3, oldTurns.Count);
        Assert.All(oldTurns[0], m => Assert.Equal("t-1", m.TurnId));
        Assert.All(oldTurns[1], m => Assert.Equal("t-2", m.TurnId));
        Assert.All(oldTurns[2], m => Assert.Equal("t-3", m.TurnId));

        // 保留尾部 2 个回合（Turn 4 和 Turn 5）
        Assert.Equal(2, keepTurns.Count);
        Assert.All(keepTurns[0], m => Assert.Equal("t-4", m.TurnId));
        Assert.All(keepTurns[1], m => Assert.Equal("t-5", m.TurnId));

        // 验证 Turn 4 内部的 ToolCall 和 Tool 响应完好无缺，没有出现孤儿
        var t4Messages = keepTurns[0];
        Assert.Equal(4, t4Messages.Count);
        var t4ToolCall = t4Messages.FirstOrDefault(m => m.ToolCalls is { Count: > 0 });
        var t4Tool = t4Messages.FirstOrDefault(m => m.Role == Role.Tool);
        Assert.NotNull(t4ToolCall);
        Assert.NotNull(t4Tool);
        Assert.Equal("call-1", t4ToolCall!.ToolCalls![0].Id);
        Assert.Equal("call-1", t4Tool!.ToolCallId);
    }

    [Fact]
    public async Task BuildLlmContext_Slices_Properly()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 5; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("第" + i + "条", CancellationToken.None);
        }

        f.Llm.EnqueueContent("要点摘要A");
        await f.Session.CompactAsync(CancellationToken.None);

        // 获取给 LLM 的切片
        var context = f.Session.BuildLlmContext();

        // 验证首项为 System Prompt
        Assert.Equal("system", context[0].RoleWire);

        // 验证次项为合成的摘要 User 消息
        Assert.Equal("user", context[1].RoleWire);
        Assert.Contains("此前会话背景与进展摘要", context[1].Content);
        Assert.Contains("要点摘要A", context[1].Content);

        // 验证后续项为 Marker 之后保留的完整回合（Turn 4, Turn 5）
        var remaining = context.Skip(2).ToList();
        Assert.Equal(4, remaining.Count);
        Assert.Equal("第3条", remaining[0].Content);
        Assert.Equal("回复3", remaining[1].Content);
        Assert.Equal("第4条", remaining[2].Content);
        Assert.Equal("回复4", remaining[3].Content);

        // 验证被折叠的历史消息（第0~2条）不在 BuildLlmContext() 产出中
        Assert.DoesNotContain(context, m => m.Content == "第0条");
        Assert.DoesNotContain(context, m => m.Content == "第1条");
        Assert.DoesNotContain(context, m => m.Content == "第2条");

        // 但在原始内存历史中，所有消息全部逐字保留！
        var memoryMsgs = f.MemoryMessages();
        Assert.Contains(memoryMsgs, m => m.Content == "第0条");
        Assert.Contains(memoryMsgs, m => m.Content == "第1条");
        Assert.Contains(memoryMsgs, m => m.Content == "第2条");
    }

    [Fact]
    public async Task Responses_Chain_Reset_On_Compact()
    {
        using var f = new AgentFixture();

        // 将 Session 协议切换为 responses
        var protoField = typeof(HaloAgent.Engine.Core.Agent.AgentSession)
            .GetField("_activeProtocol", BindingFlags.NonPublic | BindingFlags.Instance)!;
        protoField.SetValue(f.Session, "responses");

        var lastRespIdField = typeof(HaloAgent.Engine.Core.Agent.AgentSession)
            .GetField("_lastResponseId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sentCountField = typeof(HaloAgent.Engine.Core.Agent.AgentSession)
            .GetField("_responsesSentCount", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 模拟多轮对话
        for (int i = 0; i < 4; i++)
        {
            f.Llm.EnqueueContent("回复" + i);
            await f.Session.RunTurnAsync("问题" + i, CancellationToken.None);
        }

        // 模拟已建立了 responses 链路 id
        lastRespIdField.SetValue(f.Session, "resp_existing_12345");
        sentCountField.SetValue(f.Session, 8);

        Assert.Equal("resp_existing_12345", (string?)lastRespIdField.GetValue(f.Session));

        // 执行 CompactAsync
        f.Llm.EnqueueContent("压缩摘要结果");
        await f.Session.CompactAsync(CancellationToken.None);

        // 断言链路状态被强制重置
        Assert.Null(lastRespIdField.GetValue(f.Session));
        Assert.Equal(0, (int)sentCountField.GetValue(f.Session)!);
    }
}
