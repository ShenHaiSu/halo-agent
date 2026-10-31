using System.Net;
using System.Text;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>P16 回归验证（对应 docs/P16-response/fix/04-回归验证.md）。</summary>
public class P16ResponsesTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_fn(request));
    }

    private static OpenAiCompatibleClient ResponsesSseClient(params string[] dataLines)
    {
        var body = string.Concat(dataLines.Select(l => "data: " + l + "\n\n"));
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        var http = new HttpClient(handler);
        return new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "test", Protocol = LlmProtocol.Responses }, http);
    }

    private static Task<ResponsesReply> ReplayAsync(OpenAiCompatibleClient client) =>
        client.ResponsesStreamAsync(null, null, Array.Empty<object>(), null, _ => { }, CancellationToken.None);

    private const string Completed = "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\"}}";

    [Fact]
    public async Task ToolReplay_seed_delta_done_snapshot_restores_single_call_exactly_once()
    {
        // raw/sse_toolcall.txt seq 10–13 同款：added(种子) → delta(仅参数) → done(全量) → output.done(快照)
        using var client = ResponsesSseClient(
            "{\"type\":\"response.message\",\"role\":\"system\",\"content\":\"ignore\"}",
            "{\"type\":\"response.output_item.added\",\"output_index\":2,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"in_progress\",\"name\":\"list_directory\",\"call_id\":\"call_1\",\"arguments\":\"\"}}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":2,\"item_id\":\"fc_1\",\"delta\":\"{\\\"path\\\":\\\".\\\"}\"}",
            "{\"type\":\"response.function_call_arguments.done\",\"output_index\":2,\"item_id\":\"fc_1\",\"arguments\":\"{\\\"path\\\":\\\".\\\"}\",\"name\":\"list_directory\"}",
            "{\"type\":\"response.output_item.done\",\"output_index\":2,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"completed\",\"name\":\"list_directory\",\"call_id\":\"call_1\",\"arguments\":\"{\\\"path\\\":\\\".\\\"}\"}}",
            Completed);

        var reply = await ReplayAsync(client);

        Assert.Single(reply.ToolCalls);
        Assert.Equal("list_directory", reply.ToolCalls[0].Function.Name);
        Assert.Equal("{\"path\":\".\"}", reply.ToolCalls[0].Function.Arguments);
    }

    [Fact]
    public async Task ToolReplay_message_items_do_not_pollute_accumulator()
    {
        // reasoning/message 的 added/done 必须被忽略（先判 item.type==function_call）。
        using var client = ResponsesSseClient(
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"status\":\"in_progress\",\"summary\":[]}}",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"status\":\"completed\"}}",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[]}}",
            "{\"type\":\"response.output_item.added\",\"output_index\":2,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"in_progress\",\"name\":\"list_directory\",\"call_id\":\"call_1\",\"arguments\":\"\"}}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":2,\"item_id\":\"fc_1\",\"delta\":\"{\\\"path\\\":\\\".\\\"}\"}",
            "{\"type\":\"response.function_call_arguments.done\",\"output_index\":2,\"item_id\":\"fc_1\",\"arguments\":\"{\\\"path\\\":\\\".\\\"}\",\"name\":\"list_directory\"}",
            Completed);

        var reply = await ReplayAsync(client);

        Assert.Single(reply.ToolCalls);
        Assert.Equal("list_directory", reply.ToolCalls[0].Function.Name);
    }

    [Fact]
    public async Task ToolReplay_delta_only_plus_done_restores_call()
    {
        // 只有无名 delta + done（无 added 种子）→ 同样还原。
        using var client = ResponsesSseClient(
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"item_id\":\"fc_9\",\"delta\":\"{\\\"path\\\":\\\"a\\\"}\"}",
            "{\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_9\",\"arguments\":\"{\\\"path\\\":\\\"a\\\"}\",\"name\":\"read_file\"}",
            Completed);

        var reply = await ReplayAsync(client);

        Assert.Single(reply.ToolCalls);
        Assert.Equal("read_file", reply.ToolCalls[0].Function.Name);
        Assert.Equal("{\"path\":\"a\"}", reply.ToolCalls[0].Function.Arguments);
    }

    [Fact]
    public async Task ToolReplay_delta_only_without_done_is_dropped()
    {
        // 只有 delta、无 done：保持现状（丢弃，不断言还原）。
        using var client = ResponsesSseClient(
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"item_id\":\"fc_9\",\"delta\":\"{\\\"path\\\":\\\"a\\\"}\"}",
            Completed);

        var reply = await ReplayAsync(client);

        Assert.Empty(reply.ToolCalls);
    }

    [Fact]
    public async Task ToolReplay_interleaved_calls_do_not_cross_arguments()
    {
        using var client = ResponsesSseClient(
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"fc_A\",\"type\":\"function_call\",\"status\":\"in_progress\",\"name\":\"tool_a\",\"call_id\":\"call_A\",\"arguments\":\"\"}}",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"fc_B\",\"type\":\"function_call\",\"status\":\"in_progress\",\"name\":\"tool_b\",\"call_id\":\"call_B\",\"arguments\":\"\"}}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"item_id\":\"fc_A\",\"delta\":\"{\\\"x\\\":\"}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"item_id\":\"fc_B\",\"delta\":\"{\\\"y\\\":\"}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"item_id\":\"fc_A\",\"delta\":\"1}\"}",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"item_id\":\"fc_B\",\"delta\":\"2}\"}",
            "{\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"item_id\":\"fc_A\",\"arguments\":\"{\\\"x\\\":1}\",\"name\":\"tool_a\"}",
            "{\"type\":\"response.function_call_arguments.done\",\"output_index\":1,\"item_id\":\"fc_B\",\"arguments\":\"{\\\"y\\\":2}\",\"name\":\"tool_b\"}",
            Completed);

        var reply = await ReplayAsync(client);

        Assert.Equal(2, reply.ToolCalls.Count);
        var byName = reply.ToolCalls.ToDictionary(c => c.Function.Name);
        Assert.Equal("{\"x\":1}", byName["tool_a"].Function.Arguments);
        Assert.Equal("{\"y\":2}", byName["tool_b"].Function.Arguments);
    }

    [Theory]
    [InlineData(400, "referenced response not found or expired", true)]
    [InlineData(400, "previous_response_id 'resp_bogus' invalid format", true)]
    [InlineData(404, "response_not_found", true)]
    [InlineData(404, "Response expired, please start a new chain", true)]
    [InlineData(400, "invalid_previous_response", true)]
    [InlineData(400, "Previous_Response_ID mismatch", true)]
    [InlineData(401, "previous_response_id invalid", false)]
    [InlineData(500, "previous_response_id broken", false)]
    [InlineData(400, "some other error", false)]
    [InlineData(400, "", false)]
    [InlineData(404, "not a chain problem", false)]
    public void BrokenChainGuard_matches_doc_matrix(int status, string body, bool expected)
    {
        Assert.Equal(expected, AgentSession.IsBrokenChain(new EndpointStatusException(status, body)));
    }

    [Fact]
    public void BuildResponsesBody_store_defaults_true_and_prev_omitted_when_null()
    {
        using var client = ResponsesSseClient(Completed);
        var body = (Dictionary<string, object?>)client.BuildResponsesBody(null, "instr", Array.Empty<object>(), null, stream: false);
        Assert.Equal(true, body["store"]);
        Assert.DoesNotContain("previous_response_id", body);
    }

    [Fact]
    public void BuildResponsesBody_store_respects_config_and_prev_present_when_set()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "m", Protocol = LlmProtocol.Responses, StoreResponses = false }, http);
        var body = (Dictionary<string, object?>)client.BuildResponsesBody("resp_1", null, Array.Empty<object>(), null, stream: true);
        Assert.Equal(false, body["store"]);
        Assert.Equal("resp_1", body["previous_response_id"]);
    }
}
