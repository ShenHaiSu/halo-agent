using System.Reflection;
using HaloAgent.Engine.Abstractions;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Core.Tests.TestSupport;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>/3/5：Record 单调、LoadHistory 回填、ProjectSnapshot 真值优先。</summary>
public sealed class SessionTimestampTests
{
    private sealed class FakeClock : IClock
    {
        private readonly Queue<long> _script = new();
        private long _last;
        public FakeClock(long fixedNow) { _last = fixedNow; }
        public FakeClock(IEnumerable<long> script) { foreach (var t in script) _script.Enqueue(t); }
        public long UtcNowMs() => _script.Count > 0 ? _script.Dequeue() : _last;
        public void Set(long now) => _last = now;
    }

    private static void InvokeRecord(AgentSession session, ChatMessage msg)
    {
        var m = typeof(AgentSession).GetMethod("Record", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object[] { msg });
    }

    private static List<ChatMessage> MemoryOf(AgentSession session)
    {
        var f = typeof(AgentSession).GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((List<ChatMessage>)f.GetValue(session)!).ToList();
    }

    // ---- 同 ms 连发 → t, t+1, t+2 ----
    [Fact]
    public async Task Record_Stamps_Monotonic_Same_Ms()
    {
        using var f = new AgentFixture();
        var clock = new FakeClock(8000);
        using var f2 = new FixtureWithClock(clock);
        InvokeRecord(f2.Session, ChatMessage.User("a"));
        InvokeRecord(f2.Session, ChatMessage.User("b"));
        InvokeRecord(f2.Session, ChatMessage.User("c"));
        var mem = MemoryOf(f2.Session).Where(m => m.Role != Role.System).ToList();
        Assert.Equal(new[] { 8000L, 8001L, 8002L }, mem.Select(m => m.TsMs).ToArray());
        await Task.CompletedTask;
    }

    // ---- 时钟回拨 → 不倒退 ----
    [Fact]
    public void Record_Clock_Backwards_Does_Not_Regress()
    {
        var clock = new FakeClock(new[] { 9000L, 1000L });
        using var fx = new FixtureWithClock(clock);
        InvokeRecord(fx.Session, ChatMessage.User("a"));
        InvokeRecord(fx.Session, ChatMessage.User("b"));
        var mem = MemoryOf(fx.Session).Where(m => m.Role != Role.System).ToList();
        Assert.Equal(9000L, mem[0].TsMs);
        Assert.Equal(9001L, mem[1].TsMs);
    }

    // ---- 已有真值不动 ----
    [Fact]
    public void Record_Preserves_Existing_TsMs()
    {
        var clock = new FakeClock(9999);
        using var fx = new FixtureWithClock(clock);
        InvokeRecord(fx.Session, ChatMessage.User("a", tsMs: 12345));
        var mem = MemoryOf(fx.Session).Where(m => m.Role != Role.System).ToList();
        Assert.Equal(12345L, mem[0].TsMs);
    }

    // ---- 全 0 文件回填单调且 last == mtime ----
    [Fact]
    public void LoadHistory_Backfills_FromMtime()
    {
        using var fx = new AgentFixture();
        var dir = Path.GetDirectoryName(fx.Store.HistoryPath)!;
        var path = Path.Combine(dir, "p14-old.jsonl");
        var lines = new[]
        {
            "{\"meta\":{\"headless\":true,\"schema\":\"h4l0a1g2e3n4\",\"cwd\":\"C:\\\\tmp\"}}",
            "{\"role\":\"user\",\"content\":\"first\"}",
            "{\"role\":\"assistant\",\"content\":\"second\"}",
            "{\"role\":\"user\",\"content\":\"third\"}",
        };
        File.WriteAllLines(path, lines);
        var anchor = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(path, anchor.UtcDateTime);
        var anchorMs = anchor.ToUnixTimeMilliseconds();

        var (count, _) = fx.Session.LoadHistory(path);
        Assert.Equal(3, count);
        var mem = fx.MemoryMessages().Where(m => m.Role != Role.System).ToList();
        Assert.Equal(3, mem.Count);
        Assert.All(mem, m => Assert.True(m.TsMs > 0));
        Assert.True(mem[0].TsMs < mem[1].TsMs && mem[1].TsMs < mem[2].TsMs);
        Assert.Equal(anchorMs, mem[2].TsMs);
    }

    // ---- 全真透传 ----
    [Fact]
    public void ProjectSnapshot_Prefers_Real_TsMs()
    {
        var msgs = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hi", tsMs: 10000),
            ChatMessage.Assistant("hello", tsMs: 112345),
        };
        var snap = MessageProjector.ProjectSnapshot(msgs);
        Assert.Equal(2, snap.Count);
        Assert.Equal(10000, snap[0].TsMs);
        Assert.Equal(112345, snap[1].TsMs);
        Assert.Equal("m-1", snap[0].Id);
        Assert.Equal("m-2", snap[1].Id);
    }

    // ---- 全 0 降级倒推 ----
    [Fact]
    public void ProjectSnapshot_Falls_Back_For_All_Zero()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var msgs = new List<ChatMessage>
        {
            ChatMessage.User("a"),
            ChatMessage.Assistant("b"),
        };
        var snap = MessageProjector.ProjectSnapshot(msgs);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(2, snap.Count);
        Assert.All(snap, v => Assert.True(v.TsMs > 0));
        Assert.True(snap[0].TsMs < snap[1].TsMs);
        Assert.True(snap[1].TsMs <= after && snap[0].TsMs >= before - 10000);
        Assert.Equal("m-1", snap[0].Id);
    }

    // ---- ToView 透传 ----
    [Fact]
    public void ToView_Passes_Through_TsMs()
    {
        var v = MessageProjector.ToView(ChatMessage.User("x", tsMs: 424242));
        Assert.Equal(424242, v.TsMs);
        var z = MessageProjector.ToView(ChatMessage.User("y"));
        Assert.Equal(0, z.TsMs);
    }

    // ---- 集成：RunTurn 落盘 JSONL 含 ts_ms ----
    [Fact]
    public async Task RunTurn_Persists_TsMs_To_Jsonl()
    {
        using var fx = new AgentFixture();
        fx.Llm.EnqueueContent("ok");
        await fx.Session.RunTurnAsync("hi", CancellationToken.None);
        await fx.WaitForKindAsync(HaloAgent.Engine.Abstractions.Events.EventKinds.TurnFinished);
        var path = fx.Store.HistoryPath!;
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs)) text = await sr.ReadToEndAsync();
        Assert.Contains("ts_ms", text, StringComparison.Ordinal);
        // 内存时间戳全真且单调
        var mem = fx.MemoryMessages().Where(m => m.Role != Role.System).ToList();
        Assert.All(mem, m => Assert.True(m.TsMs > 0));
        for (var i = 1; i < mem.Count; i++)
            Assert.True(mem[i].TsMs > mem[i - 1].TsMs);
        // 发给模型的快照不含 ts 字段
        foreach (var call in fx.Llm.Calls)
            foreach (var m in call.Messages)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(m);
                Assert.DoesNotContain("ts_ms", json, StringComparison.OrdinalIgnoreCase);
            }
    }

    /// <summary>带 FakeClock 的最小会话夹具（避免改动公用 AgentFixture 签名）。</summary>
    private sealed class FixtureWithClock : IDisposable
    {
        private readonly string? _prev;
        public string DataDir { get; }
        public AgentSession Session { get; }
        private readonly HaloAgent.Engine.Storage.Sessions.SessionStore _store;
        private readonly EventBus _bus;
        private readonly RecordingEventSink _sink;

        public FixtureWithClock(IClock clock)
        {
            DataDir = Path.Combine(Path.GetTempPath(), "halo-p14-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDir);
            _prev = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", DataDir);
            _store = new HaloAgent.Engine.Storage.Sessions.SessionStore();
            _store.StartNew();
            _sink = new RecordingEventSink();
            _bus = new EventBus(_sink, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            _bus.Start();
            Session = new AgentSession(
                new FakeLlmClient(), ToolRegistry.CreateBuiltin(), _store, _bus,
                DataDir, DataDir, PermissionMode.BypassPermissions, string.Empty, 0, 0, true, clock);
        }

        public void Dispose()
        {
            try { _bus.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _store.Dispose(); } catch { }
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prev);
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }
}
