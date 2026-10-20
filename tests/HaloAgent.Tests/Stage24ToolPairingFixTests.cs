using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// tool-400 回归：assistant(tool_calls) 后必须紧跟 tool 块；tool 附图延迟到块尾单个 user；缺回包块尾补 synthetic。
/// </summary>
public sealed class Stage24ToolPairingFixTests
{
    private static readonly HeaderResolutionContext Ctx =
        new("ws1", "ws", "/tmp", "sess1", "hash1", "p1", "P", "gpt-4o", "req1");

    private static ModelConfig ChatVisionModel() => new()
    {
        Id = "gpt-4o", Protocol = LlmProtocolType.Chat,
        SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "high",
    };

    private static ModelConfig RespVisionModel() => new()
    {
        Id = "gpt-4o", Protocol = LlmProtocolType.Resp,
        SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "high",
    };

    private static ChatImageRef Img(string id) => new()
    {
        ImageId = id, FileName = id + ".png", Mime = "image/png",
        RelPath = "attachments/20260101/" + id + "_" + id + ".png",
        Width = 1, Height = 1, SizeBytes = 68, Sha16 = id,
    };

    private static UnifiedLlmRequest Req(ModelConfig model, List<UnifiedChatMessage> messages) => new()
    {
        RequestId = "req1",
        Provider = new ProviderConfig { Id = "p1", Name = "P", BaseUrl = "https://api.openai.com/v1" },
        Model = model,
        HeaderContext = Ctx,
        SystemInstructions = "sys",
        Messages = messages,
        Tools = Array.Empty<UnifiedToolDefinition>(),
        ImageResolver = _ => "data:image/png;base64,QUJD",
    };

    private static UnifiedChatMessage Assistant2Calls(string c1, string c2) =>
        UnifiedChatMessage.Assistant("do", new List<UnifiedToolCall>
        {
            new(c1, null, "view_image", "{}"),
            new(c2, null, "view_image", "{}"),
        });

    [Fact(DisplayName = "T24-1: Chat tool附图不插断tool块，图收齐到块尾单个user")]
    public void Chat_ToolImages_Deferred_After_ToolBlock()
    {
        var t1 = UnifiedChatMessage.Tool("c1", "shot1", "view_image");
        t1.Images = new List<ChatImageRef> { Img("img1") };
        var t2 = UnifiedChatMessage.Tool("c2", "shot2", "view_image");
        t2.Images = new List<ChatImageRef> { Img("img2") };
        var req = Req(ChatVisionModel(), new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("go"),
            Assistant2Calls("c1", "c2"), t1, t2,
        });

        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        // sys + user + assistant + tool + tool + trailing-user = 6
        Assert.Equal(6, messages.Count);
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("c1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool", messages[4].GetProperty("role").GetString());
        Assert.Equal("c2", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("user", messages[5].GetProperty("role").GetString());
        var parts = messages[5].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        string text = parts[0].GetProperty("text").GetString() ?? string.Empty;
        Assert.Contains("c1", text);
        Assert.Contains("c2", text);
        int imageParts = parts.Count(p => p.GetProperty("type").GetString() == "image_url");
        Assert.Equal(2, imageParts);
    }

    [Fact(DisplayName = "T24-2: Chat 单tool附图同样延迟，不在块中插user")]
    public void Chat_SingleToolImage_Deferred()
    {
        var t1 = UnifiedChatMessage.Tool("c1", "shot1", "view_image");
        t1.Images = new List<ChatImageRef> { Img("img1") };
        var req = Req(ChatVisionModel(), new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("go"),
            UnifiedChatMessage.Assistant("do", new List<UnifiedToolCall> { new("c1", null, "view_image", "{}") }),
            t1,
            UnifiedChatMessage.User("next"),
        });

        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        // sys,user,assistant,tool,trailing-user,user = 6，且 trailing-user 紧跟 tool
        Assert.Equal(6, messages.Count);
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("user", messages[4].GetProperty("role").GetString());
        Assert.Equal("user", messages[5].GetProperty("role").GetString());
        Assert.Equal("next", messages[5].GetProperty("content").GetString());
    }

    [Fact(DisplayName = "T24-3: Chat 缺回包发包前内存补synthetic，不发坏包")]
    public void Chat_MissingToolReply_HealedForSend()
    {
        var req = Req(ChatVisionModel(), new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("go"),
            Assistant2Calls("c1", "c2"),
            UnifiedChatMessage.Tool("c1", "ok", "view_image"),
        });

        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var tools = messages.Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("c1", tools[0].GetProperty("tool_call_id").GetString());
        Assert.Equal("c2", tools[1].GetProperty("tool_call_id").GetString());
        Assert.StartsWith("[Tool Cancelled", tools[1].GetProperty("content").GetString());
    }

    [Fact(DisplayName = "T24-4: 愈合合成追加在存量tool块尾，保持[assistant,tool,synthetic]顺序")]
    public void Heal_AppendsSynthetic_At_BlockEnd()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("go"),
            UnifiedChatMessage.Assistant("do", new List<UnifiedToolCall>
            {
                new("c1", null, "read_file", "{}"),
                new("c2", null, "read_file", "{}"),
            }),
            UnifiedChatMessage.Tool("c1", "ok", "read_file"),
        };

        var (fixed_, synthetic, removed) = WorkspaceSessionService.RebuildHealedHistory(messages);
        Assert.Equal(1, synthetic);
        Assert.Equal(0, removed);
        Assert.Equal(4, fixed_.Count);
        Assert.Equal("assistant", fixed_[1].Role);
        Assert.Equal("tool", fixed_[2].Role);
        Assert.Equal("c1", fixed_[2].ToolCallId);
        Assert.Equal("tool", fixed_[3].Role);
        Assert.Equal("c2", fixed_[3].ToolCallId);
        Assert.StartsWith("[Tool Cancelled", fixed_[3].Content);

        var guard = ChatPairingGuard.HealForSend(messages);
        Assert.Equal(4, guard.Count);
        Assert.Equal("c1", guard[2].ToolCallId);
        Assert.Equal("c2", guard[3].ToolCallId);
    }

    [Fact(DisplayName = "T24-5: Resp tool附图同样延迟到output块尾")]
    public void Resp_ToolImages_Deferred_After_OutputBlock()
    {
         var t1 = UnifiedChatMessage.Tool("c1", "shot1", "view_image");
         t1.Images = new List<ChatImageRef> { Img("img1") };
         var t2 = UnifiedChatMessage.Tool("c2", "shot2", "view_image");
         t2.Images = new List<ChatImageRef> { Img("img2") };
         var req = Req(RespVisionModel(), new List<UnifiedChatMessage>
         {
             UnifiedChatMessage.User("go"),
             Assistant2Calls("c1", "c2"), t1, t2,
         });
         string payload = new ResponsesApiAdapter().SerializeRequest(req);
         using var doc = JsonDocument.Parse(payload);
         var input = doc.RootElement.GetProperty("input").EnumerateArray().ToList();
         // user, assistant文本, function_call x2, function_call_output x2, trailing user = 7
         Assert.Equal(7, input.Count);
         Assert.Equal("function_call", input[2].GetProperty("type").GetString());
         Assert.Equal("function_call", input[3].GetProperty("type").GetString());
         Assert.Equal("function_call_output", input[4].GetProperty("type").GetString());
         Assert.Equal("function_call_output", input[5].GetProperty("type").GetString());
         Assert.Equal("message", input[6].GetProperty("type").GetString());
         Assert.Equal("user", input[6].GetProperty("role").GetString());
         var parts = input[6].GetProperty("content").EnumerateArray().ToList();
         Assert.Equal(3, parts.Count);
         Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
         Assert.Equal(2, parts.Count(p => p.GetProperty("type").GetString() == "input_image"));
    }

    [Fact(DisplayName = "T24-6: 无图载荷零抖动（tool块无尾随user）")]
    public void NoImages_NoTrailingUser_ZeroChurn()
    {
        var req = Req(ChatVisionModel(), new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("go"),
            Assistant2Calls("c1", "c2"),
            UnifiedChatMessage.Tool("c1", "ok", "view_image"),
            UnifiedChatMessage.Tool("c2", "ok", "view_image"),
        });

        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(5, messages.Count); // sys + 4，无尾随 user
        Assert.DoesNotContain("image_url", payload);
    }
}
