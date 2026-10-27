using System.Net;
using System.Text;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

public class OpenAiCompatibleClientTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        {
            _fn = fn;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_fn(request));
    }

    private static OpenAiCompatibleClient ChatClient(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(_ => JsonResponse(json, code));
        var http = new HttpClient(handler);
        return new OpenAiCompatibleClient(new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test" }, http);
    }

    private static OpenAiCompatibleClient SseClient(params string[] chunks)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(chunks), Encoding.UTF8, "text/event-stream"),
        });
        var http = new HttpClient(handler);
        return new OpenAiCompatibleClient(new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test" }, http);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code) =>
        new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static IReadOnlyList<ChatMessage> Msgs() => new[] { ChatMessage.User("hi") };

    [Fact]
    public async Task Chat_string_content_is_used_directly()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal("hello", reply.Content);
        Assert.Empty(reply.ToolCalls);
    }

    [Fact]
    public async Task Chat_array_content_is_concatenated()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"he\"},{\"type\":\"text\",\"text\":\"llo\"}]},\"finish_reason\":\"stop\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal("hello", reply.Content);
    }

    [Fact]
    public async Task Chat_null_content_with_tool_calls_is_normal()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Null(reply.Content);
        Assert.Single(reply.ToolCalls);
        Assert.Equal("read", reply.ToolCalls[0].Function.Name);
    }

    [Fact]
    public async Task Chat_object_arguments_are_serialized()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read\",\"arguments\":{\"path\":\"a\"}}}]} ,\"finish_reason\":\"tool_calls\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Single(reply.ToolCalls);
        Assert.Contains("path", reply.ToolCalls[0].Function.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_missing_tool_call_id_is_generated()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":null,\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"run\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Single(reply.ToolCalls);
        Assert.Equal("call_run", reply.ToolCalls[0].Id);
    }

    [Fact]
    public async Task Chat_usage_is_parsed()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal(15, reply.Usage.Total);
    }

    [Fact]
    public async Task Chat_reasoning_content_is_parsed()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":\"hi\",\"reasoning_content\":\"thinking\"},\"finish_reason\":\"stop\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal("thinking", reply.ReasoningContent);
    }

    [Fact]
    public async Task Chat_length_finish_reason_sets_truncated_flag()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":\"x\"},\"finish_reason\":\"length\"}]}");

        var reply = await client.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.True(reply.TruncatedByLength);
    }

    [Fact]
    public async Task Stream_done_stops_and_concatenates_content()
    {
        using var client = SseClient(
            "data: {\"choices\":[{\"delta\":{\"content\":\"he\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}\n\n",
            "data: [DONE]\n\n");

        var events = new List<StreamEvent>();
        var reply = await client.ChatStreamAsync(Msgs(), null, e => events.Add(e), CancellationToken.None);

        Assert.Equal("hello", reply.Content);
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Stream_length_finish_reason_throws()
    {
        using var client = SseClient("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChatStreamAsync(Msgs(), null, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Stream_reasoning_and_usage_are_collected()
    {
        using var client = SseClient(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}\n\n",
            "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\n\n",
            "data: [DONE]\n\n");

        var reply = await client.ChatStreamAsync(Msgs(), null, _ => { }, CancellationToken.None);

        Assert.Equal("think", reply.ReasoningContent);
        Assert.Equal(7, reply.Usage.Total);
    }

    [Fact]
    public async Task Validate_succeeds_on_ok_response()
    {
        using var client = ChatClient("{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}");

        await client.ValidateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ListModels_returns_sorted_ids()
    {
        using var client = ChatClient("{\"data\":[{\"id\":\"b-model\"},{\"id\":\"a-model\"},{\"id\":\"C-model\"}]}");

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "a-model", "b-model", "C-model" }, models.ToArray());
    }
}
