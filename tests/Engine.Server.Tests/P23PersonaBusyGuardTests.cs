using System.Reflection;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Server.Routing;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Server.Tests;

/// <summary>P23-R5：跑轮中切换人格被拒绝（前后端 busy 守卫）。</summary>
public sealed class P23PersonaBusyGuardTests : IDisposable
{
    private readonly string _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;

    public P23PersonaBusyGuardTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-srv-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dataDir, "cwd");
        Directory.CreateDirectory(_cwd);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR") ?? "";
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _dataDir);
    }

    private (AgentSession session, RealMethods real) Create()
    {
        var store = new SessionStore();
        store.StartNew();
        var tools = ToolRegistry.CreateBuiltin();
        var llm = new FakeLlm();
        var ch = new NullChannel();
        var bus = new EventBus(ch, NullLogger.Instance);
        bus.Start();
        var session = new AgentSession(llm, tools, store, bus, _dataDir, _cwd,
            HaloAgent.Engine.Abstractions.Types.PermissionMode.BypassPermissions,
            string.Empty, 0, 3, true);
        var real = new RealMethods(session, store, tools, _dataDir, NullLogger.Instance, CancellationToken.None);
        return (session, real);
    }

    private static JsonElement? P(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    private static void SetBusy(AgentSession session, bool busy)
    {
        var f = typeof(AgentSession).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(session, busy);
    }

    [Fact]
    public async Task SetPersona_Busy_Throws_StateBusy()
    {
        var (session, real) = Create();
        SetBusy(session, true);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.StateSetPersonaAsync(P(@"{""name"":""tester""}"), CancellationToken.None));
        Assert.Equal(RpcError.StateBusy, ex.Code);
    }

    [Fact]
    public async Task SetPersona_Idle_Succeeds()
    {
        var (_, real) = Create();
        var r = await real.StateSetPersonaAsync(P(@"{""name"":""tester""}"), CancellationToken.None);
        Assert.NotNull(r);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public string Model => "fake";
        public string? ProxyUrl => null;
        public Task<AssistantReply> ChatAsync(IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages, IReadOnlyList<object>? tools, CancellationToken ct, string? sessionId = null)
            => Task.FromResult(new AssistantReply { Content = "ok" });
        public Task<AssistantReply> ChatStreamAsync(IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages, IReadOnlyList<object>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
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

    private sealed class NullChannel : IFrameChannel
    {
        public string Describe => "null";
        public ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct) => new(Task.FromResult(ReceiveResult.Closed()));
        public ValueTask SendAsync(Frame frame, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
