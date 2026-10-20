using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>历史思考链默认随上下文发出（chat: reasoning_content；resp: reasoning.summary）。</summary>
public sealed class Stage23ReasoningContextTests
{
    private static UnifiedLlmRequest ChatReq(ModelConfig model, List<UnifiedChatMessage> messages) => new()
    {
        RequestId = "req_reason",
        Provider = new ProviderConfig { Id = "p1", Name = "T", BaseUrl = "https://api.openai.com/v1" },
        Model = model,
        SystemInstructions = string.Empty,
        Messages = messages,
        Tools = System.Array.Empty<UnifiedToolDefinition>(),
    };

    [Fact(DisplayName = "S23-R1: chat 默认带 reasoning_content")]
    public void Chat_Default_EmitsReasoningContent()
    {
        var model = new ModelConfig { Id = "deepseek-chat", Protocol = LlmProtocolType.Chat };
        var req = ChatReq(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done", null, "think-step"),
        });
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var assistant = messages[1];
        Assert.Equal("think-step", assistant.GetProperty("reasoning_content").GetString());
    }

    [Fact(DisplayName = "S23-R2: chat 空思考链零抖动（无字段）")]
    public void Chat_EmptyReasoning_Omitted()
    {
        var model = new ModelConfig { Id = "deepseek-chat", Protocol = LlmProtocolType.Chat };
        var req = ChatReq(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done"),
        });
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        Assert.DoesNotContain("reasoning_content", payload);
    }

    [Fact(DisplayName = "S23-R3: chat 关开关不发 reasoning_content")]
    public void Chat_OptOut_Omitted()
    {
        var model = new ModelConfig { Id = "deepseek-chat", Protocol = LlmProtocolType.Chat, IncludeReasoningInHistory = false };
        var req = ChatReq(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done", null, "think-step"),
        });
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        Assert.DoesNotContain("reasoning_content", payload);
    }

    [Fact(DisplayName = "S23-R4: chat tool_calls + reasoning 共存")]
    public void Chat_ToolCalls_WithReasoning()
    {
        var model = new ModelConfig { Id = "deepseek-chat", Protocol = LlmProtocolType.Chat };
        var calls = new List<UnifiedToolCall> { new UnifiedToolCall("c1", null, "fn", "{}") };
        var req = ChatReq(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("", calls, "think-step"),
        });
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var assistant = doc.RootElement.GetProperty("messages").EnumerateArray().ToList()[1];
        Assert.Equal("think-step", assistant.GetProperty("reasoning_content").GetString());
        Assert.Equal("c1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "S23-R5: resp 默认带 reasoning.summary")]
    public void Resp_Default_EmitsReasoningItem()
    {
        var model = new ModelConfig { Id = "o3-mini", Protocol = LlmProtocolType.Resp };
        var req = ChatReq(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done", null, "think-step"),
        });
        string payload = new ResponsesApiAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var input = doc.RootElement.GetProperty("input").EnumerateArray().ToList();
        var reasoning = input.First(e => e.GetProperty("type").GetString() == "reasoning");
        var summary = reasoning.GetProperty("summary").EnumerateArray().ToList();
        Assert.Equal("summary_text", summary[0].GetProperty("type").GetString());
        Assert.Equal("think-step", summary[0].GetProperty("text").GetString());
    }

    [Fact(DisplayName = "S23-R6: resp 空/关开关无 reasoning 项")]
    public void Resp_EmptyOrOptOut_Omitted()
    {
        var modelOn = new ModelConfig { Id = "o3-mini", Protocol = LlmProtocolType.Resp };
        var emptyReq = ChatReq(modelOn, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done"),
        });
        Assert.DoesNotContain("\"reasoning\"", new ResponsesApiAdapter().SerializeRequest(emptyReq));

        var modelOff = new ModelConfig { Id = "o3-mini", Protocol = LlmProtocolType.Resp, IncludeReasoningInHistory = false };
        var offReq = ChatReq(modelOff, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("done", null, "think-step"),
        });
        Assert.DoesNotContain("\"reasoning\"", new ResponsesApiAdapter().SerializeRequest(offReq));
    }
}
