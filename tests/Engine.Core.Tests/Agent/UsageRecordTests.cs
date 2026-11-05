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

/// <summary>落盘：每非空 turn exactly-once；异常不中断；无 Usage 不记。
/// 读写共用同一 UsageStore 实例，不依赖 HALO_DATA_DIR 二次求值（防并行竞态）。</summary>
public sealed class UsageRecordTests
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

        public string DataDir { get; }
        public FakeLlmClient Llm { get; } = new();
        public SessionStore Store { get; }
        public EventBus Bus { get; }
        public ToolRegistry Tools { get; }
        public AgentSession Session { get; }
        public UsageStore Usage { get; }

        public Scope(IEnumerable<ITool>? extraTools = null)
        {
            DataDir = Path.Combine(Path.GetTempPath(), "halo-usage-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(DataDir, "cwd");
            Directory.CreateDirectory(cwd);
            _prev = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", DataDir);
            Store = new SessionStore();
            Store.StartNew();
            Tools = extraTools is null
                ? ToolRegistry.CreateBuiltin()
                : new ToolRegistry(ToolRegistry.CreateBuiltin().All.Concat(extraTools));
            var sink = new RecordingEventSink();
            Bus = new EventBus(sink, NullLogger.Instance);
            Bus.Start();
            Usage = new UsageStore(Path.Combine(DataDir, "usage.db"));
            Session = new AgentSession(
                Llm, Tools, Store, Bus, DataDir, cwd,
                PermissionMode.BypassPermissions, string.Empty, 0, 3, true,
                usageStore: Usage);
        }

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
    public async Task Nonempty_turn_records_exactly_once()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 30); });

        await s.Session.RunTurnAsync("hi", CancellationToken.None);

        var (items, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(1, total);
        Assert.Equal(100, items[0].PromptTokens);
        Assert.Equal(30, items[0].CachedTokens);
        Assert.Equal(20, items[0].CompletionTokens);
    }

    [Fact]
    public async Task Empty_usage_records_nothing()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(0, 0, 0); });

        await s.Session.RunTurnAsync("hi", CancellationToken.None);

        var (_, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Multi_round_turn_records_once_with_sum()
    {
        using var s = new Scope(new ITool[] { new EchoTool() });
        // 第 1 round：调工具（带用量）；第 2 round：纯文本（带用量）
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
        Assert.Equal(150, items[0].PromptTokens);
        Assert.Equal(10, items[0].CachedTokens);
    }
}
