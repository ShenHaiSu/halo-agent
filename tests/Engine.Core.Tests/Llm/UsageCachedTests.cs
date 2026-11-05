using System.Net;
using System.Text;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>用量采集：CachedTokens 5 例（含流式 responses）。</summary>
public class UsageCachedTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_fn(request));
    }

    private static OpenAiCompatibleClient ChatClient(string json)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test", Protocol = LlmProtocol.Chat },
            new HttpClient(handler));
    }

    private static OpenAiCompatibleClient ResponsesClient(string json)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test", Protocol = LlmProtocol.Responses },
            new HttpClient(handler));
    }

    private static OpenAiCompatibleClient ResponsesSseClient(params string[] dataLines)
    {
        var body = string.Concat(dataLines.Select(l => "data: " + l + "\n\n"));
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        return new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test", Protocol = LlmProtocol.Responses },
            new HttpClient(handler));
    }

    [Fact]
    public async Task Chat_nonstream_with_details_cached()
    {
        using var client = ChatClient(
            "{\"choices\":[{\"message\":{\"content\":\"hi\"}}],"
            + "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":50,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":120}}}");
        var reply = await client.ChatAsync(new[] { ChatMessage.User("hi") }, null, CancellationToken.None);
        Assert.Equal(120, reply.Usage.CachedTokens);
        Assert.Equal(1000, reply.Usage.PromptTokens);
    }

    [Fact]
    public async Task Chat_nonstream_missing_cached_is_zero()
    {
        using var client = ChatClient(
            "{\"choices\":[{\"message\":{\"content\":\"hi\"}}],"
            + "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}");
        var reply = await client.ChatAsync(new[] { ChatMessage.User("hi") }, null, CancellationToken.None);
        Assert.Equal(0, reply.Usage.CachedTokens);
    }

    [Fact]
    public async Task Chat_cached_clamped_and_non_numeric_safe()
    {
        // cached(9999) > prompt(100) → 钳为 100
        using var client = ChatClient(
            "{\"choices\":[{\"message\":{\"content\":\"hi\"}}],"
            + "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10,\"cached_tokens\":9999}}");
        var reply = await client.ChatAsync(new[] { ChatMessage.User("hi") }, null, CancellationToken.None);
        Assert.Equal(100, reply.Usage.CachedTokens);

        // 非数字字符串 → 0 不抛
        using var client2 = ChatClient(
            "{\"choices\":[{\"message\":{\"content\":\"hi\"}}],"
            + "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10,\"cached_tokens\":\"abc\"}}");
        var reply2 = await client2.ChatAsync(new[] { ChatMessage.User("hi") }, null, CancellationToken.None);
        Assert.Equal(0, reply2.Usage.CachedTokens);
    }

    [Fact]
    public async Task Responses_nonstream_details_and_toplevel_compat()
    {
        using var client = ResponsesClient(
            "{\"id\":\"resp_1\",\"output\":[],"
            + "\"usage\":{\"input_tokens\":500,\"output_tokens\":60,"
            + "\"input_tokens_details\":{\"cached_tokens\":77},\"cached_tokens\":5}}");
        var reply = await client.ResponsesAsync(null, null, Array.Empty<object>(), null, CancellationToken.None);
        // details 优先
        Assert.Equal(77, reply.Usage.CachedTokens);

        using var client2 = ResponsesClient(
            "{\"response\":{\"id\":\"resp_2\",\"output\":[],"
            + "\"usage\":{\"input_tokens\":200,\"output_tokens\":20,\"prompt_cache_hit_tokens\":33}}}");
        var reply2 = await client2.ResponsesAsync(null, null, Array.Empty<object>(), null, CancellationToken.None);
        Assert.Equal(33, reply2.Usage.CachedTokens);
    }

    [Fact]
    public async Task Responses_stream_completed_with_details_cached()
    {
        using var client = ResponsesSseClient(
            "{\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_9\","
            + "\"usage\":{\"input_tokens\":800,\"output_tokens\":40,"
            + "\"input_tokens_details\":{\"cached_tokens\":200}}}}");
        var reply = await client.ResponsesStreamAsync(null, null, Array.Empty<object>(), null, _ => { }, CancellationToken.None);
        Assert.Equal(200, reply.Usage.CachedTokens);
        Assert.Equal(800, reply.Usage.PromptTokens);
    }
}
