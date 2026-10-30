using System.Net;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>P10 引擎链路验收：max_tokens/Strip/图片/protocol/headers/代理（T2 钉死 auto=直连已在 Storage 覆盖）。</summary>
public class P10LlmTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_fn(request));
    }

    private static OpenAiCompatibleClient Client(LlmConfig cfg, Func<HttpRequestMessage, HttpResponseMessage>? fn = null)
    {
        var handler = new FakeHttpHandler(fn ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json"),
        }));
        return new OpenAiCompatibleClient(cfg, new HttpClient(handler));
    }

    [Fact]
    public void Protocol_responses_throws_NotSupported()
    {
        // P13 D6 一次性放开：构造允许 responses；按协议显式分流，错配才抛。
        using var c = Client(new LlmConfig
        {
            BaseUrl = "http://localhost:11434/v1", Model = "m", Protocol = LlmProtocol.Responses,
        });
        Assert.Throws<NotSupportedException>(() => c.BuildBody(new[] { ChatMessage.User("hi") }, null, stream: false));
        var rb = c.BuildResponsesBody(null, null, Array.Empty<object>(), null, stream: false);
        Assert.NotNull(rb);
        // chat 协议调 Responses* 应抛
        using var c2 = Client(new LlmConfig { BaseUrl = "http://x/v1", Model = "m", Protocol = LlmProtocol.Chat });
        Assert.Throws<NotSupportedException>(() => c2.BuildResponsesBody(null, null, Array.Empty<object>(), null, stream: false));
    }
    [Fact]
    public void BuildBody_max_tokens_present_and_absent()
    {
        using var c1 = Client(new LlmConfig { BaseUrl = "http://x/v1", Model = "m", MaxTokens = 100 });
        var b1 = (c1.BuildBody(new[] { ChatMessage.User("hi") }, null, stream: false) as Dictionary<string, object?>)!;
        Assert.Equal(100, b1["max_tokens"]);
        Assert.False(b1.ContainsKey("max_completion_tokens"));

        using var c2 = Client(new LlmConfig { BaseUrl = "http://x/v1", Model = "m" });
        var b2 = (c2.BuildBody(new[] { ChatMessage.User("hi") }, null, stream: false) as Dictionary<string, object?>)!;
        Assert.False(b2.ContainsKey("max_tokens"));
    }


    [Fact]
    public void StripReasoning_off_copies_without_mutate()
    {
        var msgs = new[] { new ChatMessage { RoleWire = "user", Content = "hi", ReasoningContent = "r" } };
        var stripped = MessageAdapters.StripReasoning(msgs, includeHistory: false);
        Assert.Null(stripped[0].ReasoningContent);
        Assert.Equal("r", msgs[0].ReasoningContent); // 不 mutate 落盘
        var kept = MessageAdapters.StripReasoning(msgs, includeHistory: true);
        Assert.Equal("r", kept[0].ReasoningContent);
    }

    [Fact]
    public void AcceptsImages_false_blocks_image_message()
    {
        var img = ChatMessage.UserWithParts(new() { ChatContentPart.TextPart("hi"), ChatContentPart.ImagePart("data:image/png;base64,AAA") });
        Assert.True(img.HasImages);
        Assert.Throws<InvalidOperationException>(() => MessageAdapters.ValidateImages(img, acceptsImages: false));
        MessageAdapters.ValidateImages(img, acceptsImages: true); // 不抛
    }

    [Fact]
    public void Image_limits_guard()
    {
        var parts = new List<ChatContentPart> { ChatContentPart.TextPart("hi") };
        for (int i = 0; i < 9; i++) parts.Add(ChatContentPart.ImagePart("u" + i));
        var m = ChatMessage.UserWithParts(parts);
        Assert.Throws<InvalidOperationException>(() => MessageAdapters.ValidateImages(m, true));
    }

    [Fact]
    public void ChatMessage_parts_roundtrip_string_and_array()
    {
        var text = new ChatMessage { RoleWire = "user", Content = "hello" };
        var json = JsonSerializer.Serialize(text);
        Assert.Contains("\"hello\"", json);
        var back = JsonSerializer.Deserialize<ChatMessage>(json)!;
        Assert.Equal("hello", back.Content);
        Assert.Null(back.ContentParts);

        var parts = ChatMessage.UserWithParts(new() { ChatContentPart.TextPart("hi"), ChatContentPart.ImagePart("http://x/i.png") });
        var j2 = JsonSerializer.Serialize(parts);
        Assert.Contains("image_url", j2);
        var b2 = JsonSerializer.Deserialize<ChatMessage>(j2)!;
        Assert.NotNull(b2.ContentParts);
        Assert.Equal(2, b2.ContentParts!.Count);
    }

    [Fact]
    public void StripImagesForSummary_placeholder()
    {
        var m = ChatMessage.UserWithParts(new() { ChatContentPart.TextPart("see"), ChatContentPart.ImagePart("u1"), ChatContentPart.ImagePart("u2") });
        var s = MessageAdapters.StripImagesForSummary(m);
        Assert.Null(s.ContentParts);
        Assert.Contains("[图片x2]", s.Content);
        Assert.Contains("see", s.Content);
    }

    [Fact]
    public void Adapters_stored_roundtrip_parts()
    {
        var m = ChatMessage.UserWithParts(new() { ChatContentPart.TextPart("a"), ChatContentPart.ImagePart("u") });
        var stored = MessageAdapters.ToStoredMessage(m);
        Assert.NotNull(stored.ContentParts);
        var back = MessageAdapters.ToChatMessage(stored);
        Assert.True(back.HasImages);
    }

    [Fact]
    public async Task ListModels_sends_headers_and_skips_session_template()
    {
        HttpRequestMessage? captured = null;
        using var c = Client(new LlmConfig
        {
            BaseUrl = "http://x/v1",
            Model = "m",
            Headers = new[] {
                new KeyValuePair<string, string>("X-Plain", "abc"),
                new KeyValuePair<string, string>("X-T", "pre-{{session_id}}"),
            },
        }, req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"}]}", Encoding.UTF8, "application/json"),
            };
        });
        var models = await c.ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "a", "b" }, models);
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("X-Plain"));
        Assert.False(captured!.Headers.Contains("X-T")); // 探活跳过模板头
    }
}
