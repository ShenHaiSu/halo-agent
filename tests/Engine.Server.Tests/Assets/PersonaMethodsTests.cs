using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Server.Routing;
using HaloAgent.Engine.Storage.Personas;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Assets;

/// <summary>人格 RPC：删除的参数校验 / 不存在 / 路径逃逸拦截 / 已存在时落盘文件消失。</summary>
public sealed class PersonaMethodsTests : IDisposable
{
    private readonly string? _prevDataDir;
    private readonly string _dataDir;
    private readonly string _cwd;

    public PersonaMethodsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "halo-persona-srv-" + Guid.NewGuid().ToString("N"));
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

    private RealMethods Create()
    {
        var store = new SessionStore();
        store.StartNew();
        var tools = ToolRegistry.CreateBuiltin();
        var bus = new EventBus(new PersonaNullChannel(), NullLogger.Instance);
        bus.Start();
        var session = new AgentSession(new PersonaFakeLlm(), tools, store, bus, _dataDir, _cwd,
            HaloAgent.Engine.Abstractions.Types.PermissionMode.BypassPermissions,
            string.Empty, 0, 3, true);
        return new RealMethods(session, store, tools, _dataDir, NullLogger.Instance, CancellationToken.None);
    }

    private static JsonElement? P(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task Delete_rejects_blank_name()
    {
        var real = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{\"name\":\"  \"}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Delete_rejects_missing_name()
    {
        var real = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Delete_unknown_name_throws_asset_not_found()
    {
        var real = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{\"name\":\"不存在的人格-xyz\"}"), CancellationToken.None));
        Assert.Equal(RpcError.AssetNotFound, ex.Code);
    }

    [Fact]
    public async Task Delete_rejects_invalid_name_chars()
    {
        var real = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{\"name\":\"a/b\"}"), CancellationToken.None));
        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
    }

    [Fact]
    public async Task Delete_does_not_touch_file_outside_persona_dir()
    {
        var real = Create();
        var outside = Path.Combine(_dataDir, "outside.md");
        File.WriteAllText(outside, "keep me");

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{\"name\":\"../outside\"}"), CancellationToken.None));

        Assert.Equal(RpcError.ParamsInvalid, ex.Code);
        Assert.True(File.Exists(outside), "目录外文件不应被删除");
    }

    [Fact]
    public async Task Delete_existing_removes_file_and_list_item()
    {
        var real = Create();
        await real.AssetPersonasSaveAsync(P("{\"name\":\"tmp-persona\",\"body\":\"x\"}"), CancellationToken.None);

        var before = (AssetPersonasListResult)(await real.AssetPersonasListAsync(null, CancellationToken.None))!;
        Assert.Contains("tmp-persona", before.Items);

        var res = (AssetPersonasDeleteResult)(await real.AssetPersonasDeleteAsync(
            P("{\"name\":\"tmp-persona\"}"), CancellationToken.None))!;
        Assert.True(res.Ok);
        Assert.False(File.Exists(Path.Combine(PersonaStore.Dir(_dataDir), "tmp-persona.md")));

        var after = (AssetPersonasListResult)(await real.AssetPersonasListAsync(null, CancellationToken.None))!;
        Assert.DoesNotContain("tmp-persona", after.Items);
    }

    [Fact]
    public async Task Delete_same_name_twice_second_throws_not_found()
    {
        var real = Create();
        await real.AssetPersonasSaveAsync(P("{\"name\":\"dup\",\"body\":\"x\"}"), CancellationToken.None);
        await real.AssetPersonasDeleteAsync(P("{\"name\":\"dup\"}"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            real.AssetPersonasDeleteAsync(P("{\"name\":\"dup\"}"), CancellationToken.None));
        Assert.Equal(RpcError.AssetNotFound, ex.Code);
    }

    private sealed class PersonaNullChannel : IFrameChannel
    {
        public string Describe => "null";
        public ValueTask<ReceiveResult> ReceiveAsync(CancellationToken ct) =>
            new(Task.FromResult(ReceiveResult.Closed()));
        public ValueTask SendAsync(Frame frame, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PersonaFakeLlm : ILlmClient
    {
        public string Model => "fake";
        public string? ProxyUrl => null;
        public Task<AssistantReply> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, CancellationToken ct, string? sessionId = null)
            => Task.FromResult(new AssistantReply { Content = "ok" });
        public Task<AssistantReply> ChatStreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<object>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
            => Task.FromResult(new AssistantReply { Content = "ok" });
        public Task<ResponsesReply> ResponsesAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, CancellationToken ct, string? sessionId = null, bool stateless = false)
            => Task.FromResult(new ResponsesReply { Content = "ok" });
        public Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null, bool stateless = false)
            => Task.FromResult(new ResponsesReply { Content = "ok" });
        public object BuildResponsesBody(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, bool stream, bool stateless = false) => new Dictionary<string, object?>();
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        public void Reconfigure(LlmConfig cfg) { }
    }
}
