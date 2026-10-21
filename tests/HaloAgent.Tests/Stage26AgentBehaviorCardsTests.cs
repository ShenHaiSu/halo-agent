#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need26：设置中心·智能体行为·子代理卡片的后端数据源验收。
/// 背景：卡片此前展示的"轮次/工具"恒为 0/空串，"拒绝记录"恒为空（旧实现 LastRejection 从未赋值）。
/// 本类覆盖：
/// 1) SubStatsReader.ReadRuntimeStatsAsync：轮次/工具数/最近工具名必须来自真实消息。
/// 2) SubAgentCoordinator 拒绝记录环形缓冲：可记录、新的在前、超过 50 条丢最旧。
/// 3) 配额超限（SUBAGENT_QUOTA_EXCEEDED）确实落拒绝记录。
/// 4) HostBridgeFacade.GetSubRuntime：把真实拒绝记录透出给前端卡片，且运行中子代理带真实统计字段。
/// </summary>
public sealed class Stage26AgentBehaviorCardsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StoragePathManager _storageManager;
    private readonly AgentConfigService _agentService;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public Stage26AgentBehaviorCardsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage26_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_tempRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _agentService = new AgentConfigService(_storageManager);
        _service = new WorkspaceSessionService(new SessionRepository(_storageManager));
        _budgetService = new TokenBudgetService();
        _projectDir = Path.Combine(_tempRoot, "Projects", "SubAgentCards");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "子代理卡片测试")
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private SubAgentCoordinator NewCoordinator() =>
        new(
            _service,
            _budgetService,
            new TrivialGateway(),
            _storageManager,
            null,
            null,
            _agentService,
            null
        );

    private Host.Desktop.Bridge.HostBridgeFacade MakeBridge(SubAgentCoordinator? coord = null) =>
        new(
            null,
            _storageManager,
            workspaceSessionService: _service,
            agentConfigService: _agentService,
            subCoordinator: coord
        );

    // ---------- 1) 运行统计 ----------

    [Fact(DisplayName = "S26: 运行统计取真实轮次/工具数/最近工具名")]
    public async Task RuntimeStats_RealValues()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主");
        var sub = await _service.CreateSubSessionAsync(
            _workspace.Id,
            "子",
            null,
            null,
            main.Id,
            "sub",
            "标签",
            "call_x",
            "任务"
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.User("做点事")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.Assistant("先读文件")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.Tool("tc1", "内容", "read_file")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.Assistant("做完了")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.Tool("tc2", "内容2", "write_file")
        );

        var stats = await SubStatsReader.ReadRuntimeStatsAsync(_service, _workspace.Id, sub.Id);
        Assert.Equal(2, stats.Iterations); // 2 条 assistant
        Assert.Equal(2, stats.ToolCount); // 2 条 tool
        Assert.Equal("write_file", stats.LastTool); // 最近一次工具
    }

    [Fact(DisplayName = "S26: 运行统计对空会话降级为 0")]
    public async Task RuntimeStats_EmptySession()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主2");
        var stats = await SubStatsReader.ReadRuntimeStatsAsync(_service, _workspace.Id, main.Id);
        Assert.Equal(0, stats.Iterations);
        Assert.Equal(0, stats.ToolCount);
        Assert.Equal(string.Empty, stats.LastTool);
    }

    // ---------- 2) 拒绝记录环形缓冲 ----------

    [Fact(DisplayName = "S26: 拒绝记录新的在前且上限 50")]
    public void Rejections_NewestFirst_AndCapped()
    {
        var coord = NewCoordinator();
        Assert.Empty(coord.GetRecentRejections());
        for (int i = 0; i < 60; i++)
            coord.RecordRejection("CODE_" + i, "call_" + i, "m" + i);
        // 环形缓冲上限 50：最旧 10 条被丢弃
        var all = coord.GetRecentRejections(50);
        Assert.Equal(50, all.Count);
        Assert.Equal("CODE_59", all[0].Code); // 新的在前
        Assert.Equal("CODE_10", all[^1].Code); // 最旧 10 条被环形丢弃
        // 默认只回最近 20 条（卡片只需近期）
        var recent = coord.GetRecentRejections();
        Assert.Equal(20, recent.Count);
        Assert.Equal("CODE_59", recent[0].Code);
        Assert.Equal(5, coord.GetRecentRejections(5).Count);
        Assert.Equal("CODE_59", coord.GetRecentRejections(5)[0].Code);
    }

    [Fact(DisplayName = "S26: 拒绝记录含 code/callId/时间")]
    public void Rejections_RecordFields()
    {
        var coord = NewCoordinator();
        var before = DateTime.UtcNow.AddSeconds(-1);
        coord.RecordRejection("SUBAGENT_VALIDATION", "call_9", "参数非法");
        var rec = Assert.Single(coord.GetRecentRejections());
        Assert.Equal("SUBAGENT_VALIDATION", rec.Code);
        Assert.Equal("call_9", rec.CallId);
        Assert.Equal("参数非法", rec.Message);
        Assert.True(rec.AtUtc >= before);
    }

    // ---------- 3) 配额超限落记录 ----------

    [Fact(DisplayName = "S26: 并发配额超限落拒绝记录")]
    public async Task QuotaExceeded_Recorded()
    {
        var coord = NewCoordinator();
        var main = await _service.CreateSessionAsync(_workspace.Id, "主3");
        var policy = new SubPolicySnapshot { SubMaxParallel = 1 };
        var specs = new List<SpawnCallSpec>
        {
            new()
            {
                CallId = "c1",
                Task = "任务1",
                Label = "L1"
            },
            new()
            {
                CallId = "c2",
                Task = "任务2",
                Label = "L2"
            },
        };
        var (handles, rejected) = await coord.CreateSubsAsync(
            _workspace.Id,
            main.Id,
            specs,
            "p",
            "m",
            policy,
            CancellationToken.None
        );
        try
        {
            Assert.Single(rejected);
            Assert.Equal("c2", rejected[0].CallId);
            var rec = Assert.Single(coord.GetRecentRejections());
            Assert.Equal("SUBAGENT_QUOTA_EXCEEDED", rec.Code);
            Assert.Equal("c2", rec.CallId);
            Assert.Contains("SUBAGENT_QUOTA_EXCEEDED", rec.Message);
        }
        finally
        {
            coord.CancelTree(main.Id);
        }
    }

    // ---------- 4) 桥接透出 ----------

    [Fact(DisplayName = "S26: GetSubRuntime 透出真实拒绝记录")]
    public async Task GetSubRuntime_ExposesRejections()
    {
        var coord = NewCoordinator();
        coord.RecordRejection("SUBAGENT_QUOTA_EXCEEDED", "call_1", "配额超限");
        coord.RecordRejection("SUBAGENT_VALIDATION", "call_2", "参数非法");
        var bridge = MakeBridge(coord);
        var main = await _service.CreateSessionAsync(_workspace.Id, "主4");
        var r = await bridge.GetSubRuntime(_workspace.Id, main.Id);
        Assert.True(r.Ok);
        Assert.NotNull(r.Data);
        Assert.Equal(2, r.Data!.RecentRejections.Count);
        Assert.Equal("SUBAGENT_VALIDATION", r.Data.RecentRejections[0].Code); // 新的在前
        Assert.NotNull(r.Data.LastRejection);
        Assert.Equal("SUBAGENT_VALIDATION", r.Data.LastRejection!.Code);
        Assert.Equal("call_2", r.Data.LastRejection.CallId);
    }

    [Fact(DisplayName = "S26: GetSubRuntime 对无拒绝记录返回空列表（非 null）")]
    public async Task GetSubRuntime_NoRejections_EmptyList()
    {
        var bridge = MakeBridge(NewCoordinator());
        var main = await _service.CreateSessionAsync(_workspace.Id, "主5");
        var r = await bridge.GetSubRuntime(_workspace.Id, main.Id);
        Assert.True(r.Ok);
        Assert.NotNull(r.Data);
        Assert.NotNull(r.Data!.RecentRejections);
        Assert.Empty(r.Data.RecentRejections);
        Assert.Null(r.Data.LastRejection);
    }

    [Fact(DisplayName = "S26: GetSubRuntime 运行中子代理带真实统计字段")]
    public async Task GetSubRuntime_ActiveSub_HasRealStats()
    {
        var coord = NewCoordinator();
        var bridge = MakeBridge(coord);
        var main = await _service.CreateSessionAsync(_workspace.Id, "主6");
        var policy = new SubPolicySnapshot { SubMaxParallel = 1 };
        var specs = new List<SpawnCallSpec>
        {
            new()
            {
                CallId = "c1",
                Task = "任务1",
                Label = "L1"
            },
        };
        var (handles, _) = await coord.CreateSubsAsync(
            _workspace.Id,
            main.Id,
            specs,
            "p",
            "m",
            policy,
            CancellationToken.None
        );
        try
        {
            var r = await bridge.GetSubRuntime(_workspace.Id, main.Id);
            Assert.True(r.Ok);
            Assert.NotNull(r.Data);
            var item = Assert.Single(r.Data!.Subs);
            Assert.Equal(handles[0].SubSessionId, item.SubId);
            Assert.Equal("L1", item.Label);
            // 统计字段必须被真实填充（不再是硬编码 0 / 空串）
            Assert.True(item.Iteration >= 0);
            Assert.True(item.ToolCount >= 0);
            Assert.NotNull(item.LastTool);
            Assert.True(item.ElapsedMs >= 0);
        }
        finally
        {
            coord.CancelTree(main.Id);
        }
    }

    // ---- Fake 网关 ----

    private sealed class TrivialGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("ok");
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
