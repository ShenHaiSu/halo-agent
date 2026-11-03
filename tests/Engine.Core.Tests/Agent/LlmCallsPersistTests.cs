using System.Text.Json;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage.Sessions;
using HaloAgent.Engine.Storage.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>P24：turn 级 LLM 调用次数落盘（_turnLlmCalls 随 turn 落盘；零用量 turn 不落行）。</summary>
public sealed class LlmCallsPersistTests
{
    private sealed class EchoTool : ITool
    {
        public string Name => "echo_probe";
        public string Description => "test echo";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _prev;
        private bool _disposed;

        public FakeLlmClient Llm { get; } = new();
        public SessionStore Store { get; }
        public EventBus Bus { get; }
        public ToolRegistry Tools { get; }
        public AgentSession Session { get; }
        public UsageStore Usage { get; }

        public Scope(IEnumerable<ITool>? extraTools = null)
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "halo-p24-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(dataDir, "cwd");
            Directory.CreateDirectory(cwd);
            _prev = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", dataDir);
            Store = new SessionStore();
            Store.StartNew();
            Tools = extraTools is null
                ? ToolRegistry.CreateBuiltin()
                : new ToolRegistry(ToolRegistry.CreateBuiltin().All.Concat(extraTools));
            var sink = new RecordingEventSink();
            Bus = new EventBus(sink, NullLogger.Instance);
            Bus.Start();
            Usage = new UsageStore(Path.Combine(dataDir, "usage.db"));
            Session = new AgentSession(
                Llm, Tools, Store, Bus, dataDir, cwd,
                PermissionMode.BypassPermissions, string.Empty, 0, 3, true,
                usageStore: Usage);
            DataDir = dataDir;
        }

        public string DataDir { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Bus.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
            try { Store.Dispose(); }
            catch { }
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prev);
            try { Directory.Delete(DataDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Single_turn_records_llm_calls_1()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 30); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        var (items, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(1, total);
        Assert.Equal(1, items[0].LlmCalls);
        Assert.Equal(1, s.Session.BuildSnapshot().Usage.SessionLlmCalls);
    }

    [Fact]
    public async Task Multi_round_turn_sums_llm_calls()
    {
        using var s = new Scope(new ITool[] { new EchoTool() });
        s.Llm.Enqueue(r =>
        {
            r.Content = null;
            r.Usage = new Usage(100, 10, 5);
            r.AddCall("call_1", "echo_probe", "{}");
        });
        s.Llm.Enqueue(r => { r.Content = "done"; r.Usage = new Usage(50, 5, 5); });
        await s.Session.RunTurnAsync("go", CancellationToken.None);
        var (items, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(1, total);
        Assert.Equal(2, items[0].LlmCalls);
        Assert.Equal(150, items[0].PromptTokens);
        var summary = s.Usage.Summary(new UsageQuery());
        Assert.Equal(2, summary.LlmCalls);
    }

    [Fact]
    public async Task Zero_usage_round_still_counted_in_turn_row()
    {
        using var s = new Scope(new ITool[] { new EchoTool() });
        // round1：零用量但有工具调用（跳过 usage 事件块，_turnLlmCalls 必须照计）。
        s.Llm.Enqueue(r =>
        {
            r.Content = null;
            r.Usage = new Usage(0, 0, 0);
            r.AddCall("call_1", "echo_probe", "{}");
        });
        s.Llm.Enqueue(r => { r.Content = "done"; r.Usage = new Usage(50, 5, 0); });
        await s.Session.RunTurnAsync("go", CancellationToken.None);
        var (items, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(1, total);
        Assert.Equal(2, items[0].LlmCalls);
        Assert.Equal(50, items[0].PromptTokens);
    }

    [Fact]
    public async Task All_empty_usage_turn_records_nothing()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(0, 0, 0); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        var (_, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(0, total);
        // 零用量 turn 不落行：其调用次数不持久化（P24 冻结）。
        Assert.Equal(0, s.Usage.Summary(new UsageQuery()).LlmCalls);
    }

    [Fact]
    public async Task Clear_does_not_erase_db_history()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 0); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        s.Session.ClearContext();
        var summary = s.Usage.Summary(new UsageQuery { SessionId = s.Store.SessionId });
        Assert.Equal(1, summary.LlmCalls);
        Assert.Equal(1, summary.Requests);
    }
}
