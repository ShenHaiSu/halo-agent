using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>粘性无状态验收：无状态网关只交一次400税，后续不再试探 previous_response_id。</summary>
public sealed class ResponsesStatelessTests : IDisposable
{
    private readonly string? _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;

    /// <summary>模拟无状态网关：带 prev 即 400，不带即 200。</summary>
    private sealed class StatelessGatewayFake : ILlmClient
    {
        public string Model => "fake-resp";
        public string? ProxyUrl => null;
        public List<string?> PrevSeen { get; } = new();
        private int _n;
        public void Reconfigure(LlmConfig cfg) { }
        public Task<AssistantReply> ChatAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<object>? t, CancellationToken ct, string? s = null)
            => throw new NotSupportedException();
        public Task<AssistantReply> ChatStreamAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<object>? t, Action<StreamEvent> e, CancellationToken ct, string? s = null)
            => throw new NotSupportedException();
        public object BuildResponsesBody(string? p, string? i, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? t, bool s) => new Dictionary<string, object?>();
        public Task<ResponsesReply> ResponsesAsync(string? p, string? i, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? t, CancellationToken ct, string? s = null)
            => ResponsesStreamAsync(p, i, input, t, _ => { }, ct, s);
        public Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
        {
            PrevSeen.Add(previousResponseId);
            if (previousResponseId is not null)
                throw new EndpointStatusException(400, "previous_response_id 'resp_bogus' invalid format");
            _n++;
            return Task.FromResult(new ResponsesReply
            {
                ResponseId = "resp-" + _n,
                Content = "ok-" + _n,
                Usage = new Usage(10, 5),
            });
        }
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }

    private readonly StatelessGatewayFake _llm = new();
    private readonly SessionStore _store;
    private readonly EventBus _bus;
    private readonly AgentSession _session;

    public ResponsesStatelessTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-resp-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dataDir, "cwd");
        Directory.CreateDirectory(_cwd);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _dataDir);
        _store = new SessionStore();
        _store.StartNew();
        var sink = new TestSupport.RecordingEventSink();
        _bus = new EventBus(sink, NullLogger.Instance);
        _bus.Start();
        _session = new AgentSession(
            _llm, ToolRegistry.CreateBuiltin(), _store, _bus,
            _dataDir, _cwd, PermissionMode.BypassPermissions,
            string.Empty, 0, 0, false);
        _session.ReconfigureActive(new HaloAgent.Engine.Storage.Config.ResolvedActiveModel
        {
            ProviderId = "p",
            ProviderDisplay = "p",
            ModelId = "m",
            ModelDisplay = "m",
            Protocol = "responses",
            BaseUrl = "http://localhost:1/v1",
            Headers = Array.Empty<KeyValuePair<string, string>>(),
            MaxTurns = 0,
            Retry = 0,
            Persona = "",
            Configured = true,
        });
    }

    [Fact]
    public async Task StatelessGateway_OnlyFirstChainedCallPays400()
    {
        // Turn1：prev=null，直接 200。
        var r1 = await _session.RunTurnAsync("hi-1", CancellationToken.None);
        Assert.Equal("ok-1", r1.Text);
        Assert.Single(_llm.PrevSeen);
        Assert.Null(_llm.PrevSeen[0]);
        Assert.False(_session.IsResponsesStatelessForTest);

        // Turn2：先带 prev 试一次 -> 400 -> 粘性置位 -> 裸重试 200。
        var r2 = await _session.RunTurnAsync("hi-2", CancellationToken.None);
        Assert.Equal("ok-2", r2.Text);
        Assert.Equal(3, _llm.PrevSeen.Count); // [null, prev, null]
        Assert.NotNull(_llm.PrevSeen[1]);
        Assert.Null(_llm.PrevSeen[2]);
        Assert.True(_session.IsResponsesStatelessForTest);

        // Turn3：粘性生效，必须一次 200，无 400。
        int before = _llm.PrevSeen.Count;
        var r3 = await _session.RunTurnAsync("hi-3", CancellationToken.None);
        Assert.Equal("ok-3", r3.Text);
        Assert.Equal(before + 1, _llm.PrevSeen.Count);
        Assert.Null(_llm.PrevSeen[^1]);
    }

    [Fact]
    public async Task ReconfigureActive_ResetsStateless()
    {
        await _session.RunTurnAsync("a", CancellationToken.None);
        await _session.RunTurnAsync("b", CancellationToken.None);
        Assert.True(_session.IsResponsesStatelessForTest);

        _session.ReconfigureActive(new HaloAgent.Engine.Storage.Config.ResolvedActiveModel
        {
            ProviderId = "p",
            ProviderDisplay = "p",
            ModelId = "m2",
            ModelDisplay = "m2",
            Protocol = "responses",
            BaseUrl = "http://localhost:1/v1",
            Headers = Array.Empty<KeyValuePair<string, string>>(),
            MaxTurns = 0,
            Retry = 0,
            Persona = "",
            Configured = true,
        });
        Assert.False(_session.IsResponsesStatelessForTest);
    }

    public void Dispose()
    {
        try { _bus.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _store.Dispose(); } catch { }
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
