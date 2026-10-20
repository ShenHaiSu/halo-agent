#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-05/08 §1：双协议多模态序列化（无图零抖动 + 有图载荷 + 非视觉门控 + 超限取新）。</summary>
public sealed class Stage09MultimodalAdapterTests
{
    private static readonly HeaderResolutionContext Ctx =
        new("ws1", "ws", "/tmp", "sess1", "hash1", "p1", "P", "gpt-4o", "req1");

    private static UnifiedLlmRequest TextOnlyRequest(ModelConfig model)
    {
        return ImgRequest(model, new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hello"),
            UnifiedChatMessage.Assistant("hi"),
        }, null);
    }
    private static UnifiedLlmRequest ImgRequest(ModelConfig model, List<UnifiedChatMessage> messages, Func<ChatImageRef, string?>? resolver)
    {
        var provider = new ProviderConfig { Id = "p1", Name = "P", BaseUrl = "https://api.openai.com/v1" };
        return new UnifiedLlmRequest
        {
            RequestId = "req1",
            Provider = provider,
            Model = model,
            HeaderContext = Ctx,
            SystemInstructions = "sys",
            Messages = messages,
            Tools = Array.Empty<UnifiedToolDefinition>(),
            ImageResolver = resolver,
        };
    }
    private static ChatImageRef Img(string id) => new()
    {
        ImageId = id, FileName = id + ".png", Mime = "image/png",
        RelPath = "attachments/20260101/" + id + "_" + id + ".png",
        Width = 1, Height = 1, SizeBytes = 68, Sha16 = id,
    };

    [Fact(DisplayName = "S09-M1: 无图 chat 载荷 content 保持 string（零抖动）")]
    public void Chat_NoImages_ContentIsString()
    {
        var model = new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat };
        var adapter = new ChatCompletionsAdapter();
        string payload = adapter.SerializeRequest(TextOnlyRequest(model));
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal(JsonValueKind.String, messages[1].GetProperty("content").ValueKind);
        Assert.Equal("hello", messages[1].GetProperty("content").GetString());
        Assert.DoesNotContain("image_url", payload);
    }

    [Fact(DisplayName = "S09-M2: 无图 resp 载荷仅 input_text（零抖动）")]
    public void Resp_NoImages_OnlyInputText()
    {
        var model = new ModelConfig { Id = "o3-mini", Protocol = LlmProtocolType.Resp };
        var adapter = new ResponsesApiAdapter();
        string payload = adapter.SerializeRequest(TextOnlyRequest(model));
        using var doc = JsonDocument.Parse(payload);
        var input = doc.RootElement.GetProperty("input").EnumerateArray().ToList();
        var user = input[0];
        var content = user.GetProperty("content").EnumerateArray().ToList();
        Assert.Single(content);
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.DoesNotContain("input_image", payload);
    }

    [Fact(DisplayName = "S09-M3: 有图 chat 含 image_url + detail")]
    public void Chat_WithImage_EmitsImageUrl()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o", Protocol = LlmProtocolType.Chat,
            SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "high",
        };
        var msg = UnifiedChatMessage.User("看这张图");
        msg.Images = new List<ChatImageRef> { Img("img_aaa") };
        var req = ImgRequest(model, new List<UnifiedChatMessage> { msg }, _ => "data:image/png;base64,QUJD");
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var content = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var parts = content.EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,QUJD", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("high", parts[1].GetProperty("image_url").GetProperty("detail").GetString());
    }

    [Fact(DisplayName = "S09-M4: 有图 resp 含 input_image")]
    public void Resp_WithImage_EmitsInputImage()
    {
        var model = new ModelConfig
        {
            Id = "o3-mini", Protocol = LlmProtocolType.Resp,
            SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "low",
        };
        var msg = UnifiedChatMessage.User("看这张图");
        msg.Images = new List<ChatImageRef> { Img("img_bbb") };
        var req = ImgRequest(model, new List<UnifiedChatMessage> { msg }, _ => "UVFC");
        string payload = new ResponsesApiAdapter().SerializeRequest(req);
        using var doc = JsonDocument.Parse(payload);
        var input = doc.RootElement.GetProperty("input").EnumerateArray().ToList();
        var parts = input[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
        Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,UVFC", parts[1].GetProperty("image_url").GetString());
    }

    [Fact(DisplayName = "S09-M5: 非视觉模型含图抛 NonVisionModelException（双协议）")]
    public void NonVision_WithImages_Throws()
    {
        var chatModel = new ModelConfig { Id = "m", Protocol = LlmProtocolType.Chat, SupportsVision = false };
        var respModel = new ModelConfig { Id = "m", Protocol = LlmProtocolType.Resp, SupportsVision = false };
        var msg = UnifiedChatMessage.User("hi");
        msg.Images = new List<ChatImageRef> { Img("img_ccc") };
        var chatReq = ImgRequest(chatModel, new List<UnifiedChatMessage> { msg }, _ => "data:image/png;base64,QUJD");
        var respReq = ImgRequest(respModel, new List<UnifiedChatMessage> { msg }, _ => "data:image/png;base64,QUJD");
        var chatEx = Assert.Throws<NonVisionModelException>(() => new ChatCompletionsAdapter().SerializeRequest(chatReq));
        Assert.Equal(1, chatEx.ImageCount);
        Assert.Throws<NonVisionModelException>(() => new ResponsesApiAdapter().SerializeRequest(respReq));
    }

    [Fact(DisplayName = "S09-M6: 超 Max 取最新 N（保 latest 丢最旧）")]
    public void OverLimit_KeepsLatest_DropsOldest()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o", Protocol = LlmProtocolType.Chat,
            SupportsVision = true, MaxImagesPerTurn = 1,
        };
        var m1 = UnifiedChatMessage.User("first");
        m1.Images = new List<ChatImageRef> { Img("img_old1"), Img("img_old2") };
        var m2 = UnifiedChatMessage.User("second");
        m2.Images = new List<ChatImageRef> { Img("img_new") };
        var req = ImgRequest(model, new List<UnifiedChatMessage> { m1, m2 }, img => "data:image/png;base64," + img.ImageId);
        string payload = new ChatCompletionsAdapter().SerializeRequest(req);
        Assert.DoesNotContain("img_old1", payload);
        Assert.DoesNotContain("img_old2", payload);
        Assert.Contains("img_new", payload);
        int count = CountOccurrences(payload, "\"type\":\"image_url\"");
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "S09-M7: tools 按 name 升序含 4 新工具")]
    public void Tools_Sorted_IncludesNewTools()
    {
        var registry = new ToolRegistry();
        Assert.Contains("view_image", registry.ToolNames);
        Assert.Contains("ask_user", registry.ToolNames);
        Assert.Contains("skill_tool", registry.ToolNames);
        Assert.Contains("memory_tool", registry.ToolNames);
        string chatJson = registry.GetChatToolsJson();
        int ask = chatJson.IndexOf("ask_user", StringComparison.Ordinal);
        int mem = chatJson.IndexOf("memory_tool", StringComparison.Ordinal);
        int skill = chatJson.IndexOf("skill_tool", StringComparison.Ordinal);
        int view = chatJson.IndexOf("view_image", StringComparison.Ordinal);
        Assert.True(ask >= 0 && mem > ask && skill > mem && view > skill);
        string respJson = registry.GetResponsesToolsJson();
        Assert.Contains("ask_user", respJson);
        Assert.Contains("view_image", respJson);
    }

     private static int CountOccurrences(string text, string needle)
     {
         int count = 0, idx = 0;
         while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
         {
             count++;
             idx += needle.Length;
         }
         return count;
     }
 
     private static byte[] TinyPng() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
 
     [Fact(DisplayName = "N11-1: 真盘读盘→dataURL（Chat 含 data: 前缀）")]
     public void Need11_RealDisk_ChatContainsDataUrl()
     {
         string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "n11_" + System.Guid.NewGuid().ToString("N"));
         string ws = "ws", sess = "sess";
         try
         {
             Host.Desktop.LlmGateway.Models.ImageDataRegistry.Clear();
             var (r, _) = Host.Desktop.Infrastructure.Storage.ImageAttachmentStore.SaveImage(root, ws, sess, "a.png", "image/png", TinyPng(), 1, 1);
             var model = new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat, SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "high" };
             var msg = UnifiedChatMessage.User("看这张图"); msg.Images = new List<ChatImageRef> { r };
             var req = ImgRequest(model, new List<UnifiedChatMessage> { msg }, img => Host.Desktop.Infrastructure.Storage.ImageAttachmentStore.ResolveDataUrl(root, ws, sess, img));
             string payload = new ChatCompletionsAdapter().SerializeRequest(req);
             using var doc = JsonDocument.Parse(payload);
             var parts = doc.RootElement.GetProperty("messages").EnumerateArray().ToList()[1].GetProperty("content").EnumerateArray().ToList();
             Assert.Equal(2, parts.Count);
             Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetProperty("url").GetString());
         }
         finally { try { System.IO.Directory.Delete(root, true); } catch { } }
     }
 
     [Fact(DisplayName = "N11-2: 丢失附件静默丢图保文（双协议）")]
     public void Need11_MissingFile_TextOnly()
     {
         string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "n11_" + System.Guid.NewGuid().ToString("N"));
         string ws = "ws", sess = "sess";
         try
         {
             var (r, _) = Host.Desktop.Infrastructure.Storage.ImageAttachmentStore.SaveImage(root, ws, sess, "b.png", "image/png", TinyPng(), 1, 1);
             string? abs = Host.Desktop.Infrastructure.Storage.ImageAttachmentStore.FindImagePath(root, ws, sess, r.ImageId);
             Assert.NotNull(abs); System.IO.File.Delete(abs!);
             System.Func<ChatImageRef, string?> resolver = img => Host.Desktop.Infrastructure.Storage.ImageAttachmentStore.ResolveDataUrl(root, ws, sess, img);
             var chatModel = new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat, SupportsVision = true, MaxImagesPerTurn = 4 };
             var respModel = new ModelConfig { Id = "o3", Protocol = LlmProtocolType.Resp, SupportsVision = true, MaxImagesPerTurn = 4 };
             var m1 = UnifiedChatMessage.User("文字仍在"); m1.Images = new List<ChatImageRef> { r };
             var m2 = UnifiedChatMessage.User("文字仍在"); m2.Images = new List<ChatImageRef> { r };
             string chatPayload = new ChatCompletionsAdapter().SerializeRequest(ImgRequest(chatModel, new List<UnifiedChatMessage> { m1 }, resolver));
             string respPayload = new ResponsesApiAdapter().SerializeRequest(ImgRequest(respModel, new List<UnifiedChatMessage> { m2 }, resolver));
             Assert.DoesNotContain("image_url", chatPayload); Assert.Contains("6587", chatPayload);
             Assert.DoesNotContain("input_image", respPayload); Assert.Contains("6587", respPayload);
             using (var cdoc = JsonDocument.Parse(chatPayload))
             {
                 var cm = cdoc.RootElement.GetProperty("messages").EnumerateArray().ToList();
                 Assert.Equal("文字仍在", cm[1].GetProperty("content").GetString());
             }
         }
         finally { try { System.IO.Directory.Delete(root, true); } catch { } }
     }
 
     [Fact(DisplayName = "N11-3: Responses 补 detail + tool 附图块尾 input_text（FIX-tool-400：图延迟到 output 块尾单个 user）")]
     public void Need11_Resp_DetailAndToolText()
     {
         var model = new ModelConfig { Id = "o3", Protocol = LlmProtocolType.Resp, SupportsVision = true, MaxImagesPerTurn = 4, VisionDetail = "low" };
         var um = UnifiedChatMessage.User("看这张图"); um.Images = new List<ChatImageRef> { Img("img_n11") };
         var req = ImgRequest(model, new List<UnifiedChatMessage> { um }, _ => "data:image/png;base64,QUJD");
         using var doc = JsonDocument.Parse(new ResponsesApiAdapter().SerializeRequest(req));
         var parts = doc.RootElement.GetProperty("input").EnumerateArray().ToList()[0].GetProperty("content").EnumerateArray().ToList();
         Assert.Equal("low", parts[1].GetProperty("detail").GetString());
         // tool 附图必须配对在 assistant(function_call) 之后；图收齐到 output 块尾单个 user
         var assistant = UnifiedChatMessage.Assistant(null, new List<UnifiedToolCall> { new("c1", null, "read_file", "{}") });
         var tool = UnifiedChatMessage.Tool("c1", "已读取 a.png（1x1）"); tool.Images = new List<ChatImageRef> { Img("img_n11") };
         var treq = ImgRequest(model, new List<UnifiedChatMessage> { assistant, tool }, _ => "data:image/png;base64,QUJD");
         using var tdoc = JsonDocument.Parse(new ResponsesApiAdapter().SerializeRequest(treq));
         var tinput = tdoc.RootElement.GetProperty("input").EnumerateArray().ToList();
         Assert.Equal(3, tinput.Count);
         Assert.Equal("function_call", tinput[0].GetProperty("type").GetString());
         Assert.Equal("function_call_output", tinput[1].GetProperty("type").GetString());
         var tparts = tinput[2].GetProperty("content").EnumerateArray().ToList();
         Assert.Equal("input_text", tparts[0].GetProperty("type").GetString());
         Assert.False(string.IsNullOrWhiteSpace(tparts[0].GetProperty("text").GetString()));
         Assert.Equal("input_image", tparts[1].GetProperty("type").GetString());
         Assert.Equal("low", tparts[1].GetProperty("detail").GetString());
         var assistant2 = UnifiedChatMessage.Assistant(null, new List<UnifiedToolCall> { new("c2", null, "read_file", "{}") });
         var emptyTool = UnifiedChatMessage.Tool("c2", "  "); emptyTool.Images = new List<ChatImageRef> { Img("img_n11") };
         using var edoc = JsonDocument.Parse(new ResponsesApiAdapter().SerializeRequest(ImgRequest(model, new List<UnifiedChatMessage> { assistant2, emptyTool }, _ => "data:image/png;base64,QUJD")));
         var eparts = edoc.RootElement.GetProperty("input").EnumerateArray().ToList()[2].GetProperty("content").EnumerateArray().ToList();
         Assert.Contains("c2", eparts[0].GetProperty("text").GetString());
     }
}
