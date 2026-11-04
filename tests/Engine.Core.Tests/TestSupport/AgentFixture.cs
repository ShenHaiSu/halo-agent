using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage.Sessions;
using HaloAgent.Engine.Storage.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.TestSupport;

/// <summary>
/// agent 测试夹具：一次性搭好 llm/tools/store/bus，且默认用临时数据目录。
/// 不用 DI 容器：每个测试精确控制依赖，手写 new 能一眼看出依赖什么。
///
/// 数据目录隔离：构造时把 HALO_DATA_DIR 指到临时目录（Paths 每次读取环境变量），
/// 析构时恢复原值并删除目录。SessionStore 的路径在 StartNew 时固定，Memory 工具
/// 用 Host.DataDir（本夹具字段），因此与其它并行夹具互不干扰。
/// </summary>
public sealed class AgentFixture : IDisposable
{
    private static readonly FieldInfo MessagesField =
        typeof(AgentSession).GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string? _prevDataDir;
    private bool _disposed;

    public string DataDir { get; }
    public string Cwd { get; }
    public FakeLlmClient Llm { get; }
    public SessionStore Store { get; }
    public RecordingEventSink Sink { get; }
    public EventBus Bus { get; }
    public ToolRegistry Tools { get; }
    public AgentSession Session { get; }
    /// <summary>P24：P0-2。传进 Session 的 UsageStore，测试可直接查落库行数（不断言文件）。</summary>
    public UsageStore Usage { get; }

    public AgentFixture(
        PermissionMode mode = PermissionMode.BypassPermissions,
        bool interactive = true,
        int maxTurns = 0,
        int retry = 3,
        IEnumerable<ITool>? extraTools = null)
    {
        DataDir = Path.Combine(Path.GetTempPath(), "halo-ag-" + Guid.NewGuid().ToString("N"));
        Cwd = Path.Combine(DataDir, "cwd");
        Directory.CreateDirectory(Cwd);

        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", DataDir);

        Store = new SessionStore();
        Store.StartNew();

        Tools = extraTools is null
            ? ToolRegistry.CreateBuiltin()
            : new ToolRegistry(ToolRegistry.CreateBuiltin().All.Concat(extraTools));

        Llm = new FakeLlmClient();
        Sink = new RecordingEventSink();
        Bus = new EventBus(Sink, NullLogger.Instance);
        Bus.Start();

        Usage = new UsageStore(Path.Combine(DataDir, "usage.db"));
        Session = new AgentSession(
            Llm, Tools, Store, Bus, DataDir, Cwd, mode, string.Empty, maxTurns, retry, interactive,
            usageStore: Usage);
    }

     /// <summary>历史序列（排除 system 提示词），即真正会发给端点的内容。</summary>
     /// <remarks>经反射读内存（任务允许的两种方式之一）。不用文件的原因：
     /// 生产侧 StoredMessage.Role 缺少 [JsonIgnore]，与 RoleWire 的 "role"
     /// 在 snake_case 下冲突，序列化恒抛并被 Record 的 catch 吞掉，
     /// 落盘文件恒只有 meta 行。内存序列与落盘是同一次 Record 追加，顺序一致。
     /// 生产 bug 已在报告里单独注明。</remarks>
     public IReadOnlyList<ChatMessage> MessagesForTest()
     {
         return MemoryMessages().Where(m => m.Role != Role.System).ToList();
     }

     /// <summary>内存历史（含压缩后的状态与 system 首条）。仅压缩测试用。</summary>
     public List<ChatMessage> MemoryMessages()
     {
         var list = (List<ChatMessage>)MessagesField.GetValue(Session)!;
         return list.ToList();
     }

    /// <summary>断言历史序列合法（等价于红线 R-2）。</summary>
    public void AssertInvariants(IReadOnlyList<ChatMessage>? messages = null)
    {
        var list = messages ?? MessagesForTest();
        var violations = MessageSequenceValidator.Validate(list);
        Assert.True(violations.Count == 0, "历史序列非法：\n" + string.Join("\n", violations));
    }

    /// <summary>录制到的事件 kind 序列。</summary>
    public string[] EventTrace() => Sink.Kinds();

    /// <summary>等待某事件到达（EventBus 异步写出，直接断言会 flaky）。</summary>
    public async Task WaitForKindAsync(string kind, int timeoutMs = 5000)
    {
        var hit = await TryWaitForKindAsync(kind, timeoutMs).ConfigureAwait(false);
        if (hit is null)
            throw new TimeoutException($"等待事件超时: {kind}（已有: {string.Join(",", Sink.Kinds())}）");
    }
    /// <summary>P24：P0-4。等待某事件到达，超时返回 null（不抛），供 R2 断言 payload 用。</summary>
    public async Task<RecordingEventSink.RecordedEvent?> TryWaitForKindAsync(string kind, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = Sink.Snapshot().FirstOrDefault(e => e.Kind == kind);
            if (hit is not null) return hit;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>P24：P0-4。把 RecordedEvent 的 Payload 反序列化为 C# 载荷（camelCase 命名策略）。</summary>
    public static T PayloadOf<T>(RecordingEventSink.RecordedEvent ev)
    {
        if (ev.Payload is null) throw new InvalidOperationException("事件无 payload");
        return JsonSerializer.Deserialize<T>(ev.Payload.Value.GetRawText(), HaloJson.Options)
            ?? throw new InvalidOperationException($"事件 payload 反序列化失败: {typeof(T).Name}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Bus.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
        try { Store.Dispose(); }
        catch { }
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(DataDir, recursive: true); }
        catch { }
    }
}
