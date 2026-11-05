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

/// <summary>水位线后端：估算公式 / 重置语义 / 计数器递增 / 事件透传 / 快照两段到达。</summary>
public sealed class UsageWaterlineTests
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
        public RecorderSink Sink { get; } = new();
        public EventBus Bus { get; }
        public ToolRegistry Tools { get; }
        public AgentSession Session { get; }
        public UsageStore Usage { get; }

        public Scope(IEnumerable<ITool>? extraTools = null)
        {
            DataDir = Path.Combine(Path.GetTempPath(), "halo-p23-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(DataDir, "cwd");
            Directory.CreateDirectory(cwd);
            _prev = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", DataDir);
            Store = new SessionStore();
            Store.StartNew();
            Tools = extraTools is null
                ? ToolRegistry.CreateBuiltin()
                : new ToolRegistry(ToolRegistry.CreateBuiltin().All.Concat(extraTools));
            Bus = new EventBus(Sink, NullLogger.Instance);
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

        public sealed class RecorderSink : HaloAgent.Engine.Abstractions.Frames.IFrameChannel
        {
            private readonly object _gate = new();
            private readonly List<(string Kind, JsonElement? Payload)> _events = new();
            public string Describe => "recorder";
            public ValueTask<HaloAgent.Engine.Abstractions.Frames.ReceiveResult> ReceiveAsync(CancellationToken ct) =>
                new(Task.FromResult(HaloAgent.Engine.Abstractions.Frames.ReceiveResult.Closed()));
            public ValueTask SendAsync(HaloAgent.Engine.Abstractions.Frames.Frame frame, CancellationToken ct)
            {
                if (frame is HaloAgent.Engine.Abstractions.Frames.EventFrame ev)
                {
                    lock (_gate) _events.Add((ev.Kind, ev.Payload));
                }
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public IReadOnlyList<(string Kind, JsonElement? Payload)> Snapshot()
            {
                lock (_gate) return _events.ToArray();
            }
        }
    }

    [Fact]
    public void Estimator_empty_ascii_cjk()
    {
        Assert.Equal(1, TokenEstimator.Estimate(null));
        Assert.Equal(1, TokenEstimator.Estimate(string.Empty));
        Assert.Equal(1, TokenEstimator.Estimate("abcd"));
        Assert.Equal(2, TokenEstimator.Estimate("abcdefgh"));
        Assert.Equal(1, TokenEstimator.Estimate("你好世界"));
        Assert.Equal(3, TokenEstimator.Estimate(new string('x', 9)));
    }

    [Fact]
    public void New_session_snapshot_has_estimate_and_zero_calls()
    {
        using var s = new Scope();
        var snap = s.Session.BuildSnapshot();
        Assert.NotNull(snap.Usage.EstimatedInitialTokens);
        Assert.True(snap.Usage.EstimatedInitialTokens > 0);
        Assert.Null(snap.Usage.LastPromptTokens);
        Assert.Null(snap.Usage.LastCachedTokens);
        Assert.Equal(0, snap.Usage.SessionLlmCalls);
    }

    [Fact]
    public async Task Turn_sets_lastprompt_and_calls_and_event_carries_cached_llmcalls()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 30); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        // 给 EventBus 异步写出留时间
        await Task.Delay(300);
        var snap = s.Session.BuildSnapshot();
        Assert.Equal(100, snap.Usage.LastPromptTokens);
        Assert.Equal(30, snap.Usage.LastCachedTokens);
        Assert.Equal(1, snap.Usage.SessionLlmCalls);
        // turn 内求和落盘（单 round：和=末轮）
        var (items, total) = s.Usage.Query(new UsageQuery());
        Assert.Equal(1, total);
        Assert.Equal(100, items[0].PromptTokens);

        var usageEvents = s.Sink.Snapshot().Where(e => e.Kind == "usage").ToList();
        Assert.NotEmpty(usageEvents);
        var last = usageEvents[^1].Payload!.Value;
        Assert.Equal(100, last.GetProperty("prompt").GetInt64());
        Assert.Equal(30, last.GetProperty("cached").GetInt64());
        Assert.Equal(1, last.GetProperty("llmCalls").GetInt64());
    }

    [Fact]
    public async Task Multi_round_turn_takes_last_prompt_but_sums_for_store()
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
        var snap = s.Session.BuildSnapshot();
        // 末轮覆盖：50，而非求和 150
        Assert.Equal(50, snap.Usage.LastPromptTokens);
        Assert.Equal(2, snap.Usage.SessionLlmCalls);
        var (items, _) = s.Usage.Query(new UsageQuery());
        Assert.Equal(150, items[0].PromptTokens);
    }

    [Fact]
    public async Task Empty_reply_dropped_still_counts_llmcall()
    {
        using var s = new Scope();
        // 空回复中间态：第一次返回空 Usage（计 1 次后 continue），第二次成功
        s.Llm.Enqueue(r => { r.Content = null; r.Usage = new Usage(10, 0, 0); });
        s.Llm.Enqueue(r => { r.Content = "ok"; r.Usage = new Usage(12, 3, 0); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        var snap = s.Session.BuildSnapshot();
        Assert.Equal(2, snap.Usage.SessionLlmCalls);
    }

    [Fact]
    public async Task Cancel_does_not_count()
    {
        using var s = new Scope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        s.Llm.Enqueue(r => { r.Content = "never"; r.Usage = new Usage(10, 1, 0); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.Session.RunTurnAsync("hi", cts.Token));
        Assert.Equal(0, s.Session.BuildSnapshot().Usage.SessionLlmCalls);
    }

    [Fact]
    public async Task StartNew_and_Clear_reset_to_estimate()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 0); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        Assert.Equal(1, s.Session.BuildSnapshot().Usage.SessionLlmCalls);

        s.Session.StartNewSession();
        var snap = s.Session.BuildSnapshot();
        Assert.Null(snap.Usage.LastPromptTokens);
        Assert.Null(snap.Usage.LastCachedTokens);
        Assert.Equal(0, snap.Usage.SessionLlmCalls);
        Assert.NotNull(snap.Usage.EstimatedInitialTokens);

        s.Llm.Enqueue(r => { r.Content = "hi2"; r.Usage = new Usage(200, 20, 0); });
        await s.Session.RunTurnAsync("hi2", CancellationToken.None);
        s.Session.ClearContext();
        var snap2 = s.Session.BuildSnapshot();
        Assert.Null(snap2.Usage.LastPromptTokens);
        Assert.Equal(0, snap2.Usage.SessionLlmCalls);
        Assert.NotNull(snap2.Usage.EstimatedInitialTokens);
    }

    [Fact]
    public async Task LoadHistory_resets_calls_and_keeps_last_empty()
    {
        using var s = new Scope();
        s.Llm.Enqueue(r => { r.Content = "hi"; r.Usage = new Usage(100, 20, 0); });
        await s.Session.RunTurnAsync("hi", CancellationToken.None);
        // 当前历史文件路径即新会话文件
        var path = s.Store.HistoryPath!;
        var (count, _) = s.Session.LoadHistory(path);
        Assert.True(count >= 0);
        var snap = s.Session.BuildSnapshot();
        Assert.Null(snap.Usage.LastPromptTokens);
        Assert.Equal(0, snap.Usage.SessionLlmCalls);
        Assert.NotNull(snap.Usage.EstimatedInitialTokens);
    }

    [Fact]
    public async Task Compact_does_not_reset_waterline_or_calls()
    {
        using var s = new Scope(new ITool[] { new EchoTool() });
        // 造 3 个 turn（每 turn 1 次 LLM 调用），使 compact 有可折叠回合
        for (var i = 0; i < 3; i++)
        {
            s.Llm.Enqueue(r => { r.Content = $"t{i}"; r.Usage = new Usage(100 + i, 5, 0); });
            await s.Session.RunTurnAsync($"q{i}", CancellationToken.None);
        }
        var before = s.Session.BuildSnapshot();
        Assert.Equal(3, before.Usage.SessionLlmCalls);
        // compact 摘要走 ChatAsync（非 Stream），提供摘要内容
        s.Llm.Enqueue(r => { r.Content = "摘要要点"; });
        var r = await s.Session.CompactAsync(CancellationToken.None);
        Assert.True(r.RemovedTurns >= 0);
        var after = s.Session.BuildSnapshot();
        Assert.Equal(before.Usage.LastPromptTokens, after.Usage.LastPromptTokens);
        Assert.Equal(before.Usage.SessionLlmCalls, after.Usage.SessionLlmCalls);
    }
}
