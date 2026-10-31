using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Server.Routing;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Server.Tests;

/// <summary>P10-fix3 C/E：SessionSetCwdAsync busy 守卫 + 异常映射 + 去引号。</summary>
public sealed class P10Fix3SetCwdServerTests : IDisposable
{
    private readonly string _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;

    public P10Fix3SetCwdServerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-srv-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_dataDir, "cwd");
        Directory.CreateDirectory(_cwd);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR") ?? "";
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _dataDir);
    }

    private (AgentSession session, SessionStore store, RealMethods real) Create()
    {
        var store = new SessionStore();
        store.StartNew();
        var tools = ToolRegistry.CreateBuiltin();
        var llm = new FakeLlmForServer();
        var ch = new NullChannel();
        var bus = new EventBus(ch, NullLogger.Instance);
        bus.Start();
        var session = new AgentSession(llm, tools, store, bus, _dataDir, _cwd,
            HaloAgent.Engine.Abstractions.Types.PermissionMode.BypassPermissions,
            string.Empty, 0, 3, true);
        var real = new RealMethods(session, store, tools, _dataDir, NullLogger.Instance, CancellationToken.None);
        return (session, store, real);
    }

    private static JsonElement? P(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task SetCwd_MissingDir_Maps_FsNotFound()
    {
        var (_, _, real) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.SessionSetCwdAsync(P(@"{""path"":""C:\\no-such-dir-xyz-12345""}"), CancellationToken.None));
        Assert.Equal(RpcError.FsNotFound, ex.Code);
    }

    [Fact]
    public async Task SetCwd_Empty_Maps_ParamsInvalid()
    {
        var (_, _, real) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.SessionSetCwdAsync(P(@"{""path"":""""}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task SetCwd_Root_Maps_ParamsInvalid()
    {
        var (_, _, real) = Create();
        var root = Path.GetPathRoot(_cwd)!;
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.SessionSetCwdAsync(P(JsonSerializer.Serialize(new { path = root })), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task SetCwd_Quoted_Succeeds()
    {
        var (session, _, real) = Create();
        var target = Path.Combine(_dataDir, "quoted");
        Directory.CreateDirectory(target);
        var r = await real.SessionSetCwdAsync(P(JsonSerializer.Serialize(new { path = "\"" + target + "\"" })), CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal(Path.GetFullPath(target), session.Cwd);
    }

    [Fact]
    public void UnquoteCwd_Strips_Paired_Quotes()
    {
        Assert.Equal(@"C:\a", RealMethods.UnquoteCwd(@"""C:\a"""));
        Assert.Equal(@"C:\a", RealMethods.UnquoteCwd(@"'C:\a'"));
        Assert.Equal(@"C:\a", RealMethods.UnquoteCwd(@"  C:\a  "));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private sealed class FakeLlmForServer : ILlmClient
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
