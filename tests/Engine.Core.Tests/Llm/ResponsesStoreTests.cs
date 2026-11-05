using System.Net;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Storage.Config;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>P26 三档语义 + Provider 链路 + 不变式（对应 docs/P26-response-fix/05-tests-acceptance.md）。</summary>
public class ResponsesStoreTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_fn(request));
    }

    private static Dictionary<string, object?> BuildBody(StoreMode mode, bool stateless, string? instructions = "instr")
    {
        using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new OpenAiCompatibleClient(
            new LlmConfig { BaseUrl = "http://localhost:11434/v1", Model = "m", Protocol = LlmProtocol.Responses, StoreMode = mode }, http);
        return (Dictionary<string, object?>)client.BuildResponsesBody(null, instructions, Array.Empty<object>(), null, stream: true, stateless: stateless);
    }

    // ---- R1（D1）：缺省档不得发送 store 键 ----
    [Fact]
    public void R1_Default_mode_omits_store_key_entirely()
    {
        // 不显式配置 StoreMode —— 模拟 EngineAssembly / ReconfigureActive 的真实构造方式
        using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = "http://localhost:11434/v1",
            Model = "m",
            Protocol = LlmProtocol.Responses,
            // 故意不写 StoreMode：默认值即生产行为（StoreMode.Default）
        }, http);
        var d = (Dictionary<string, object?>)client.BuildResponsesBody(
            null, "instr", Array.Empty<object>(), null, stream: true);
        // ★ 三档核心：Default = 键不存在，而不是 store=false 或 store=null
        Assert.False(d.ContainsKey("store"), "缺省档必须省略 store 键（omit store）");
    }

    // ---- R1b（三档全量） ----
    [Theory]
    [InlineData(StoreMode.Default, null)]
    [InlineData(StoreMode.True, true)]
    [InlineData(StoreMode.False, false)]
    public void R1b_three_modes_map_to_expected_body(StoreMode mode, bool? expectedStore)
    {
        var d = BuildBody(mode, stateless: false);
        if (expectedStore is null) Assert.False(d.ContainsKey("store"));
        else Assert.Equal(expectedStore.Value, d["store"]);
    }

    // ---- R2b（D3 负例）：不得误降级 ----
    [Theory]
    [InlineData(400, "model 'gpt-9' does not exist", false)]
    [InlineData(400, "invalid api key", false)]
    [InlineData(404, "restore point not found", false)]
    [InlineData(400, "storage quota exceeded", false)]
    [InlineData(400, "store: OpenCode doesn't store responses", true)]
    [InlineData(400, "store is not supported by this endpoint", true)]
    [InlineData(400, "please omit store", true)]
    public void R2b_BrokenChain_guard_matches_store_matrix(int status, string body, bool expected)
    {
        Assert.Equal(expected, AgentSession.IsBrokenChain(new EndpointStatusException(status, body)));
    }

    // ---- R3（D4+INV-4）：无状态模式强制 store:false ----
    [Fact]
    public void R3_Stateless_mode_forces_store_false_even_if_provider_says_true()
    {
        var d = BuildBody(StoreMode.True, stateless: true);
        Assert.True(d.ContainsKey("store"));           // ★ 必须显式发，不能省略
        Assert.False((bool)d["store"]!);               // ★ INV-4：降级恒 false
        Assert.DoesNotContain("previous_response_id", d);
    }

    // ---- R3b（三档 × stateless 组合矩阵） ----
    [Theory]
    [InlineData(StoreMode.True, true, false)]
    [InlineData(StoreMode.False, true, false)]
    [InlineData(StoreMode.Default, true, false)]   // ★ 缺省 + 降级 → 也必须显式 false（不省略）
    [InlineData(StoreMode.Default, false, null)]    // 缺省 + 不降级 → 省略
    public void R3b_stateless_overrides_all_modes_to_false(StoreMode cfg, bool stateless, bool? expected)
    {
        var d = BuildBody(cfg, stateless);
        if (expected is null) Assert.False(d.ContainsKey("store"));
        else Assert.Equal(expected.Value, d["store"]);
    }

    // ---- R4（D2 + 三档）：Provider 配置链路端到端 ----
    [Theory]
    [InlineData(null, StoreMode.Default)]
    [InlineData("default", StoreMode.Default)]
    [InlineData("true", StoreMode.True)]
    [InlineData("false", StoreMode.False)]
    [InlineData("YES", StoreMode.Default)]   // 非法值 → 回落 Default，不抛
    public void R4_provider_response_store_resolves_to_store_mode(string? raw, StoreMode expected)
    {
         var cfgJson = raw is null
             ? "{\"providers\":{\"p\":{\"base_url\":\"http://x/v1\",\"models\":[{\"id\":\"m\",\"protocol\":\"responses\"}]}},\"active_provider\":\"p\",\"active_model\":\"m\"}"
             : "{\"providers\":{\"p\":{\"base_url\":\"http://x/v1\",\"response_store\":\"" + raw + "\",\"models\":[{\"id\":\"m\",\"protocol\":\"responses\"}]}},\"active_provider\":\"p\",\"active_model\":\"m\"}";
        var cfg = JsonSerializer.Deserialize<ConfigModel>(cfgJson)!;
        var resolved = ConfigStore.Resolve(cfg);
        Assert.Equal(expected, resolved.StoreMode);          // ★ D2：解析产物必须正确

        // 全链路闭合：resolved → LlmConfig → 请求体
        using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = resolved.BaseUrl,
            Model = resolved.ModelId,
            Protocol = LlmProtocol.Responses,
            StoreMode = resolved.StoreMode,
        }, http);
        var d = (Dictionary<string, object?>)client.BuildResponsesBody(null, null, Array.Empty<object>(), null, true);
        if (expected == StoreMode.Default) Assert.False(d.ContainsKey("store"));
        else Assert.Equal(expected == StoreMode.True, d["store"]);
    }

    // ---- R6（跨端一致性） ----
    [Fact]
    public void R6_backend_response_store_values_match_frontend()
    {
        Assert.Equal("default", ResponseStores.Default);
        Assert.Equal("true", ResponseStores.True);
        Assert.Equal("false", ResponseStores.False);
        Assert.True(ResponseStores.IsValid("default"));
        Assert.False(ResponseStores.IsValid("Default"));   // ★ 严格小写，不做大小写宽容
    }

    // ---- 不变式 ----
    [Theory]
    [InlineData(StoreMode.True)]
    [InlineData(StoreMode.False)]
    [InlineData(StoreMode.Default)]
    public void INV4_stateless_never_sends_store_true(StoreMode cfg)
    {
        var d = BuildBody(cfg, stateless: true);                 // 恒为 false
        Assert.Equal(false, d["store"]);
    }

    [Fact]
    public void INV3_default_mode_has_no_store_key()
    {
        var d = BuildBody(StoreMode.Default, stateless: false);
        Assert.False(d.ContainsKey("store"));                    // 键不存在，不是 null
    }

    [Fact]
    public void INV6_stateless_keeps_instructions()
    {
        var d = BuildBody(StoreMode.Default, stateless: true, instructions: "SYS");
        Assert.Equal("SYS", d["instructions"]);                  // 无状态不得丢 system
    }
}
