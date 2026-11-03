using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>fix-model-switch 回归：ReconfigureActive 必须置位 Configured/ApiKeySet（P18/P22 重写时丢失两行）。</summary>
public sealed class ReconfigureActiveConfiguredTests : IDisposable
{
    private readonly string? _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;
    private readonly SessionStore _store;
    private readonly EventBus _bus;
    private readonly AgentSession _session;
    private sealed class NoopLlm : ILlmClient
    {
        public string Model => "fake";
        public string? ProxyUrl => null;
        public void Reconfigure(LlmConfig cfg) { }
        public Task<AssistantReply> ChatAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<object>? t, CancellationToken ct, string? s = null)
            => Task.FromResult(new AssistantReply { Content = "ok" });
        public Task<AssistantReply> ChatStreamAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<object>? t, Action<StreamEvent> e, CancellationToken ct, string? s = null)
            => Task.FromResult(new AssistantReply { Content = "ok" });
        public object BuildResponsesBody(string? p, string? i, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? t, bool s) => new Dictionary<string, object?>();
        public Task<ResponsesReply> ResponsesAsync(string? p, string? i, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? t, CancellationToken ct, string? s = null)
            => Task.FromResult(new ResponsesReply { Content = "ok" });
        public Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
            => Task.FromResult(new ResponsesReply { Content = "ok" });
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }

    public ReconfigureActiveConfiguredTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-recfg-" + Guid.NewGuid().ToString("N"));
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
            new NoopLlm(), ToolRegistry.CreateBuiltin(), _store, _bus,
            _dataDir, _cwd, PermissionMode.BypassPermissions,
            string.Empty, 0, 0, false);
    }

    private static ResolvedActiveModel Resolved(string provider = "p", string model = "m", bool configured = true, string? apiKey = "k")
        => new()
        {
            ProviderId = provider,
            ProviderDisplay = provider,
            ModelId = model,
            ModelDisplay = model,
            Protocol = "chat",
            BaseUrl = "http://localhost:1/v1",
            ApiKey = apiKey,
            Headers = Array.Empty<KeyValuePair<string, string>>(),
            MaxTurns = 0,
            Retry = 0,
            Persona = "",
            Configured = configured,
        };

    [Fact]
    public void ReconfigureActive_SetsConfiguredAndApiKeySet()
    {
        _session.ReconfigureActive(Resolved(apiKey: "secret"));
        var agent = _session.BuildSnapshot().Agent;
        Assert.True(agent.Configured);
        Assert.Equal("p", agent.Provider);
        Assert.Equal("m", agent.Model);
        Assert.True(agent.ApiKeySet);
    }

    [Fact]
    public void ReconfigureActive_EmptyApiKey_ApiKeySetFalse_ButConfiguredTrue()
    {
        _session.ReconfigureActive(Resolved(apiKey: null));
        var agent = _session.BuildSnapshot().Agent;
        Assert.True(agent.Configured);
        Assert.False(agent.ApiKeySet);
    }

     [Fact]
     public void ClearActive_ResetsConfigured()
     {
         _session.ReconfigureActive(Resolved());
         Assert.True(_session.BuildSnapshot().Agent.Configured);
         _session.ClearActive();
         var agent = _session.BuildSnapshot().Agent;
         Assert.False(agent.Configured);
         Assert.Equal("", agent.Provider);
         Assert.False(agent.ApiKeySet);
     }

    public void Dispose()
    {
        try { _bus.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _store.Dispose(); } catch { }
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
