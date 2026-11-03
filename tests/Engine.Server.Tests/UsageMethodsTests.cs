 using System.Text.Json;
 using HaloAgent.Engine.Abstractions.Frames;
 using HaloAgent.Engine.Abstractions.Types;
 using HaloAgent.Engine.Core.Agent;
 using HaloAgent.Engine.Core.Events;
 using HaloAgent.Engine.Core.Llm;
 using HaloAgent.Engine.Core.Tools;
 using HaloAgent.Engine.Server.Routing;
 using HaloAgent.Engine.Storage.Sessions;
 using HaloAgent.Engine.Storage.Usage;
 using Microsoft.Extensions.Logging.Abstractions;
 using Xunit;

namespace HaloAgent.Engine.Server.Tests;

/// <summary>P16 用量 RPC：校验规则（fromMs&gt;toMs / order=asc / clear未来时间）+ 写入后可查。</summary>
public sealed class UsageMethodsTests : IDisposable
{
    private readonly string? _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;

    public UsageMethodsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-usage-srv-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dataDir, "cwd");
        Directory.CreateDirectory(_cwd);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _dataDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dataDir, recursive: true); }
        catch { }
    }

    private RealMethods Create(out UsageStore usage)
    {
        var store = new SessionStore();
        store.StartNew();
        var tools = ToolRegistry.CreateBuiltin();
        var llm = new UsageFakeLlm();
        var ch = new UsageNullChannel();
        var bus = new EventBus(ch, NullLogger.Instance);
        bus.Start();
        var session = new AgentSession(llm, tools, store, bus, _dataDir, _cwd,
            HaloAgent.Engine.Abstractions.Types.PermissionMode.BypassPermissions,
            string.Empty, 0, 3, true);
        usage = new UsageStore(Path.Combine(_dataDir, "usage.db"));
        return new RealMethods(session, store, tools, _dataDir, NullLogger.Instance, CancellationToken.None, usage);
    }

    private static JsonElement? P(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task Query_rejects_from_after_to()
    {
        var real = Create(out _);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.UsageQueryAsync(P("{\"fromMs\":2000,\"toMs\":1000}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Query_rejects_asc_order()
    {
        var real = Create(out _);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.UsageQueryAsync(P("{\"order\":\"asc\"}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Clear_rejects_future_beforeMs()
    {
        var real = Create(out _);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.UsageClearAsync(P("{\"beforeMs\":99999999999999}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Clear_rejects_missing_beforeMs()
    {
        var real = Create(out _);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.UsageClearAsync(P("{}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Record_then_summary_and_stats_roundtrip()
    {
        var real = Create(out var usage);
        usage.Record(new UsageEvent
        {
            TsMs = 1000, SessionId = "s1", TurnId = "t1",
            ProviderId = "openai", ModelId = "m", Protocol = "chat",
            PromptTokens = 100, CachedTokens = 25, CompletionTokens = 10,
            TotalTokens = 110, LlmCalls = 3, RecordedMs = 1000,
        });

        dynamic? s = await real.UsageSummaryAsync(P("{}"), CancellationToken.None);
        Assert.NotNull(s);
        Assert.Equal(100L, (long)s!.GetType().GetProperty("input")!.GetValue(s)!);
        Assert.Equal(0.25, (double)s!.GetType().GetProperty("hitRate")!.GetValue(s)!);
        Assert.Equal(3L, (long)s!.GetType().GetProperty("llmCalls")!.GetValue(s)!);

        dynamic? st = await real.UsageStatsAsync(null, CancellationToken.None);
        Assert.NotNull(st);
        Assert.Equal(1L, (long)st!.GetType().GetProperty("rowCount")!.GetValue(st)!);

        dynamic? q = await real.UsageQueryAsync(P("{\"page\":1,\"pageSize\":50}"), CancellationToken.None);
        Assert.NotNull(q);
        Assert.Equal(1L, (long)q!.GetType().GetProperty("total")!.GetValue(q)!);
        var items = (System.Collections.IEnumerable)q!.GetType().GetProperty("items")!.GetValue(q)!;
        object? first = null;
        foreach (var it in items) { first = it; break; }
        Assert.NotNull(first);
        Assert.Equal(3L, (long)first!.GetType().GetProperty("llmCalls")!.GetValue(first)!);
    }
}
 
    internal sealed class UsageFakeLlm : ILlmClient
     {
         public string Model => "fake";
         public string? ProxyUrl => null;
         public Task<AssistantReply> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, CancellationToken ct, string? sessionId = null)
             => Task.FromResult(new AssistantReply { Content = "ok" });
         public Task<AssistantReply> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
             => Task.FromResult(new AssistantReply { Content = "ok" });
         public Task<ResponsesReply> ResponsesAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, CancellationToken ct, string? sessionId = null)
             => Task.FromResult(new ResponsesReply { Content = "ok" });
         public Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
             => Task.FromResult(new ResponsesReply { Content = "ok" });
         public object BuildResponsesBody(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, bool stream) => new Dictionary<string, object?>();
         public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
         public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
         public void Reconfigure(LlmConfig cfg) { }
     }
 
    internal sealed class UsageNullChannel : HaloAgent.Engine.Abstractions.Frames.IFrameChannel
     {
         public string Describe => "null";
         public ValueTask<HaloAgent.Engine.Abstractions.Frames.ReceiveResult> ReceiveAsync(CancellationToken ct) => new(Task.FromResult(HaloAgent.Engine.Abstractions.Frames.ReceiveResult.Closed()));
         public ValueTask SendAsync(HaloAgent.Engine.Abstractions.Frames.Frame frame, CancellationToken ct) => ValueTask.CompletedTask;
         public ValueTask DisposeAsync() => ValueTask.CompletedTask;
     }
