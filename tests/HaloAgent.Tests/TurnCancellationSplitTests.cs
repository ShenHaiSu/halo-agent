#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Services.Agent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

// 并行隔离：TurnCancellation.LiveLinkedSources 是进程级静态计数器，
// 并行下其它 orchestrator 用例会交错增减导致泄漏断言 flake（与 Serial-ImageCost 同策略）。
[CollectionDefinition("Serial-CancelSplit", DisableParallelization = true)]
public sealed class SerialCancelSplitCollection { }

/// <summary>
/// 取消源拆分回归：主轮超时不取消子、用户取消仍级联、子超时独立于主、linked CTS 零泄漏。
/// 每个用例在 revert 对应改动后必须失败；时间参数留出裕度，避免 CI 抖动假失败。
/// </summary>
[Collection("Serial-CancelSplit")]
public sealed class TurnCancellationSplitTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public TurnCancellationSplitTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentCancelSplit_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentCancelSplitBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
        _projectDir = Path.Combine(_tempRoot, "Projects", "CancelSplit");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "取消源拆分测试")
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    private AgentConfigService NewConfigService() => new(_storageManager);

    /// <summary>
    /// 独立存储的配置服务：主与 coordinator 各持一份，避免后写覆盖前者（决策5分别注入）。
    /// </summary>
    private AgentConfigService NewIsolatedConfigService(int turnTimeoutMs)
    {
        var root = Path.Combine(_tempRoot, "cfg-" + Guid.NewGuid().ToString("N"));
        var boot = Path.Combine(root, "boot");
        Directory.CreateDirectory(boot);
        var sm = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(boot, "bootstrap.json"),
            customDefaultDataRoot: root,
            customFallbackDataRoot: root
        );
        var cfg = new AgentConfigService(sm);
        cfg.SaveAgentConfig(Config(turnTimeoutMs));
        return cfg;
    }

    private static AgentConfigDocument Config(int turnTimeoutMs) =>
        new()
        {
            ExecDefaultTimeoutMs = 30000,
            ExecMaxTimeoutMs = 300000,
            MaxIterations = 25,
            TurnTimeoutMs = turnTimeoutMs,
            SubBarrierBufferMs = 1000,
            SubAgentEnabled = true,
            SubMaxParallel = 2,
        };

    private SubAgentCoordinator NewCoordinator(AgentConfigService cfg, ILlmGatewayClient gateway) =>
        new(_service, _budgetService, gateway, _storageManager, null, null, cfg, null);

    // ---------- 用例 1：主轮超时不取消子，子完整总结照常回填 ----------

    [Fact(DisplayName = "主轮超时不取消子，子完整总结回填主会话")]
    public async Task MainTurnTimeout_DoesNotCancelChildren()
    {
        var mainCfg = NewConfigService();
        mainCfg.SaveAgentConfig(Config(2000));
        // 独立存储：coordinator 的子超时 10s，不会被主 2s 覆盖（决策5分别注入）
        var coordCfg = NewIsolatedConfigService(10000);
        var main = await _service.CreateSessionAsync(_workspace.Id, "主-超时");
        var sink = new RecordingSink();
        var gateway = new SpawnThenSlowChildGateway(main.Id, childDelayMs: 5000);
        var coordinator = NewCoordinator(coordCfg, gateway);

        try
        {
            // 主首轮 spawn 后立即进入 Barrier（用 User 等子）；主 Turn 2s 超时会取消 6 阶段，
            // 但 Barrier 与回填用 User，故子继续跑到 5s 完成并把完整总结写回主会话。
            var result = await RunMainOrchAsync(
                _service,
                _budgetService,
                _storageManager,
                mainCfg,
                gateway,
                coordinator,
                _workspace.Id,
                main.Id,
                "用子代理做事",
                sink
            );

            // 主轮以 TIMEOUT 收尾（既有判定不变）
            Assert.Equal("TIMEOUT", result.ErrorCode);
            Assert.True(
                sink.Has(sessionId: main.Id, kind: "TurnError", payloadContains: "TIMEOUT")
            );

            // 子跑完且完整总结落在主会话，无截断标记
            await WaitFor(
                async () =>
                {
                    var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
                    return msgs.Any(m =>
                        m.Role == "tool"
                        && m.Name == "spawn_subagent"
                        && (m.Content ?? string.Empty).Contains("子完整总结")
                    );
                },
                15000
            );
            var finalMsgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
            var spawnTool = finalMsgs.FirstOrDefault(m =>
                m.Role == "tool"
                && m.Name == "spawn_subagent"
                && (m.Content ?? string.Empty).Contains("子完整总结")
            );
            Assert.NotNull(spawnTool);
            Assert.DoesNotContain("[子超时截断", spawnTool!.Content ?? string.Empty);

            // 子会话终态为 succeeded（旧代码会被主 token 掐断成 cancelled）
            var subs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
            Assert.NotEmpty(subs);
            Assert.All(subs, s => Assert.Equal("succeeded", s.SubStatus));
        }
        finally
        {
            mainCfg.ResetAgentConfig(null);
            coordCfg.ResetAgentConfig(null);
        }
    }

    // ---------- 用例 2：用户取消仍级联所有子 ----------

    [Fact(DisplayName = "用户取消级联所有子，主会话 TurnCancelled，无子在跑")]
    public async Task UserCancel_StillCascadesToAllChildren()
    {
        var mainCfg = NewConfigService();
        // TurnTimeoutMs=0 排除超时干扰
        mainCfg.SaveAgentConfig(Config(0));
        var main = await _service.CreateSessionAsync(_workspace.Id, "主-用户取消");
        var sink = new RecordingSink();
        var gate = new SpawnHoldGateway(main.Id);
        var coordinator = NewCoordinator(mainCfg, gate);

        try
        {
            using var cts = new CancellationTokenSource();
            var turn = RunMainOrchAsync(
                _service,
                _budgetService,
                _storageManager,
                mainCfg,
                gate,
                coordinator,
                _workspace.Id,
                main.Id,
                "并行做事",
                sink,
                cts.Token
            );

            // 等子起来后取消
            await gate.WaitSubStartedAsync(10000);
            cts.Cancel();
            var result = await turn;

            Assert.True(result.WasCancelled || result.ErrorCode == "TIMEOUT" || !result.Success);

            var subs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
            Assert.NotEmpty(subs);
            await WaitFor(
                async () =>
                {
                    var cur = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
                    return cur.All(s => s.SubStatus == "cancelled");
                },
                10000
            );
            var finalSubs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
            Assert.All(finalSubs, s => Assert.Equal("cancelled", s.SubStatus));
            Assert.True(sink.Has(main.Id, "TurnCancelled", null));
        }
        finally
        {
            mainCfg.ResetAgentConfig(null);
            gate.ReleaseAll();
        }
    }

    // ---------- 用例 3：子超时独立于主超时 ----------

    [Fact(DisplayName = "子超时独立触发，主轮不受影响")]
    public async Task ChildTimeoutIndependentOfMain()
    {
        var mainCfg = NewConfigService();
        // 主 20s（主存储）、子 3s（独立存储）：子先在自己会话报 TIMEOUT；主轮此期间不报 TIMEOUT。
        mainCfg.SaveAgentConfig(Config(20000));
        var coordCfg = NewIsolatedConfigService(3000);
        var main = await _service.CreateSessionAsync(_workspace.Id, "主-子独立超时");
        var sink = new RecordingSink();
        var gateway = new SpawnThenHungChildGateway(main.Id);
        var coordinator = NewCoordinator(coordCfg, gateway);

        try
        {
            var result = await RunMainOrchAsync(
                _service,
                _budgetService,
                _storageManager,
                mainCfg,
                gateway,
                coordinator,
                _workspace.Id,
                main.Id,
                "用子代理跑长任务",
                sink
            );

            // 子在自己会话因自己的 3s 独立超时而 Failed("TIMEOUT")：
            // 断言子会话终态 failed（MapSubStatus 映射），全程不碰全局事件队列以免干扰其他测试。
            var subs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
            Assert.NotEmpty(subs);
            Assert.All(subs, s => Assert.Equal("failed", s.SubStatus));

            // 子超时结果照常回填到主会话 tool message（JSON 含 TIMEOUT 错误码，证明主轮未截断子结果）
            await WaitFor(
                async () =>
                {
                    var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
                    return msgs.Any(m =>
                        m.Role == "tool"
                        && m.Name == "spawn_subagent"
                        && (m.Content ?? string.Empty).Contains("TIMEOUT")
                    );
                },
                10000
            );
            var mainMsgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
            Assert.Contains(
                mainMsgs,
                m =>
                    m.Role == "tool"
                    && m.Name == "spawn_subagent"
                    && (m.Content ?? string.Empty).Contains("TIMEOUT")
            );

            // 主轮没被子的 TIMEOUT 带偏：正常完成，主会话无 TurnError。
            Assert.True(result.Success, $"主轮应正常完成: err={result.ErrorMessage}");
            Assert.NotEqual("TIMEOUT", result.ErrorCode);
            Assert.False(sink.Has(main.Id, "TurnError", "TIMEOUT"));
        }
        finally
        {
            mainCfg.ResetAgentConfig(null);
            coordCfg.ResetAgentConfig(null);
            gateway.ReleaseAll();
        }
    }

    // ---------- 用例 4：linked CTS 零泄漏 ----------

    [Fact(DisplayName = "大量轮次下 linked CTS 零泄漏，载体只创建一个 CTS")]
    public async Task NoTokenLeak_UnderManyTurns()
    {
        // 等待前序测试残留的后台 CTS 收敛，取稳定基线（避免跨测试基线漂移）。
        int baseLine = await WaitStableLiveCountAsync();
        // (a) 直接构造/Dispose 100 次；存活期恒为基线+1，Dispose 后回到基线。
        for (int i = 0; i < 100; i++)
        {
            using (
                var tokens = new TurnCancellation(CancellationToken.None, CancellationToken.None)
            )
            {
                Assert.Equal(baseLine + 1, TurnCancellation.LiveLinkedSources);
                _ = tokens.Linked;
                _ = tokens.Linked; // 多次访问同一个 CTS，不新增
                Assert.Equal(baseLine + 1, TurnCancellation.LiveLinkedSources);
            }
            Assert.Equal(baseLine, TurnCancellation.LiveLinkedSources);
        }

        // (b) 跑一次含 spawn 的完整主轮，收敛后计数回到基线。
        var mainCfg = NewConfigService();
        mainCfg.SaveAgentConfig(Config(0));
        var main = await _service.CreateSessionAsync(_workspace.Id, "主-泄漏");
        var gateway = new QuickSpawnGateway(main.Id);
        var coordinator = NewCoordinator(mainCfg, gateway);
        try
        {
            var result = await RunMainOrchAsync(
                _service,
                _budgetService,
                _storageManager,
                mainCfg,
                gateway,
                coordinator,
                _workspace.Id,
                main.Id,
                "spawn 一个子",
                new RecordingSink()
            );
            Assert.True(result.Success);
            int after = await WaitStableLiveCountAsync();
            Assert.Equal(baseLine, after);
        }
        finally
        {
            mainCfg.ResetAgentConfig(null);
        }
    }

    /// <summary>轮询等待全局计数稳定（残留后台 CTS 收敛），返回稳定值。</summary>
    private static async Task<int> WaitStableLiveCountAsync(int timeoutMs = 3000)
    {
        int last = TurnCancellation.LiveLinkedSources;
        int stableRounds = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && stableRounds < 5)
        {
            await Task.Delay(30);
            int cur = TurnCancellation.LiveLinkedSources;
            stableRounds = cur == last ? stableRounds + 1 : 0;
            last = cur;
        }
        return last;
    }

    // ---------- helpers ----------

    private static async Task WaitFor(Func<Task<bool>> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await cond())
                return;
            await Task.Delay(20);
        }
    }

    private static async Task<AgentTurnResult> RunMainOrchAsync(
        WorkspaceSessionService service,
        TokenBudgetService budget,
        StoragePathManager storage,
        AgentConfigService mainCfg,
        ILlmGatewayClient gateway,
        SubAgentCoordinator? coordinator,
        string workspaceId,
        string sessionId,
        string text,
        IAgentEventSink sink,
        CancellationToken ct = default
    )
    {
        var policy = new SubPolicySnapshot(mainCfg.GetInternal());
        var orch = new AgentOrchestrator(
            service,
            budget,
            gateway,
            ToolRoleRegistries.ForMainRole(policy),
            storage,
            agentConfigService: mainCfg,
            coordinator: coordinator,
            subPolicy: policy,
            agentRole: "main",
            sink: sink
        );
        return await orch.RunAgentTurnAsync(workspaceId, sessionId, text, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>按 sessionId 记录轮事件，供断言 TurnError/TurnCancelled。</summary>
    private sealed class RecordingSink : IAgentEventSink
    {
        private readonly object _lock = new();
        private readonly List<(string SessionId, string Kind, string Payload)> _events = new();

        public Task EmitAsync(string sessionId, string kind, object? payload)
        {
            lock (_lock)
                _events.Add((sessionId, kind, payload?.ToString() ?? string.Empty));
            return Task.CompletedTask;
        }

        public void EmitStreamDelta(
            string sessionId,
            string kind,
            string textDelta,
            string? blockId = null,
            long seq = 0,
            string? tag = null
        ) { }

        public bool Has(string sessionId, string kind, string? payloadContains)
        {
            lock (_lock)
                return _events.Any(e =>
                    e.SessionId == sessionId
                    && e.Kind == kind
                    && (payloadContains == null || e.Payload.Contains(payloadContains))
                );
        }
    }

    /// <summary>
    /// 主首轮回 spawn tool_call；主第二轮永久挂起（等超时）；子延迟后给完整总结。
    /// 按 HeaderContext.SessionId 区分主/子。
    /// </summary>
    private sealed class SpawnThenSlowChildGateway : ILlmGatewayClient
    {
        private readonly string _mainSessionId;
        private readonly int _childDelayMs;
        private int _mainCall;
        private int _childCall;

        public SpawnThenSlowChildGateway(string mainSessionId, int childDelayMs)
        {
            _mainSessionId = mainSessionId;
            _childDelayMs = childDelayMs;
        }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            bool isMain = string.Equals(
                request.HeaderContext.SessionId,
                _mainSessionId,
                StringComparison.Ordinal
            );
            if (isMain)
            {
                int n = Interlocked.Increment(ref _mainCall);
                if (n == 1)
                {
                    yield return UnifiedStreamEvent.ToolDone(
                        "call_spawn_main",
                        "spawn_subagent",
                        "{\"task\":\"产出子完整总结\",\"label\":\"慢子\"}"
                    );
                    yield return UnifiedStreamEvent.Completed();
                    yield break;
                }
                // 主第二轮挂起，等待主轮 Turn 超时
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                yield return UnifiedStreamEvent.Text("不应到达");
                yield break;
            }
            Interlocked.Increment(ref _childCall);
            await Task.Delay(_childDelayMs, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            yield return UnifiedStreamEvent.Text("子完整总结：一切完成。");
            yield return UnifiedStreamEvent.Completed();
        }
    }

    /// <summary>用户取消用例：主首轮 spawn，子挂起直到取消；主第二轮也挂起。</summary>
    private sealed class SpawnHoldGateway : ILlmGatewayClient
    {
        private readonly string _mainSessionId;
        private readonly TaskCompletionSource _subStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _mainCall;

        public SpawnHoldGateway(string mainSessionId) => _mainSessionId = mainSessionId;

        public Task WaitSubStartedAsync(int timeoutMs) =>
            Task.WhenAny(_subStarted.Task, Task.Delay(timeoutMs));

        public void ReleaseAll() => _subStarted.TrySetResult();

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            bool isMain = string.Equals(
                request.HeaderContext.SessionId,
                _mainSessionId,
                StringComparison.Ordinal
            );
            if (isMain)
            {
                int n = Interlocked.Increment(ref _mainCall);
                if (n == 1)
                {
                    yield return UnifiedStreamEvent.ToolDone(
                        "call_spawn_hold",
                        "spawn_subagent",
                        "{\"task\":\"长任务\",\"label\":\"挂起子\"}"
                    );
                    yield return UnifiedStreamEvent.Completed();
                    yield break;
                }
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                yield break;
            }
            _subStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            yield return UnifiedStreamEvent.Text("不应到达");
            yield break;
        }
    }

    /// <summary>子超时用例：子持续 streaming 超 3s；主首轮 spawn 后挂起等子。</summary>
    private sealed class SpawnThenHungChildGateway : ILlmGatewayClient
    {
        private readonly string _mainSessionId;
        private int _mainCall;

        public SpawnThenHungChildGateway(string mainSessionId) => _mainSessionId = mainSessionId;

        public int MainCallCount => Volatile.Read(ref _mainCall);

        public void ReleaseAll() { }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            bool isMain = string.Equals(
                request.HeaderContext.SessionId,
                _mainSessionId,
                StringComparison.Ordinal
            );
            if (isMain)
            {
                int n = Interlocked.Increment(ref _mainCall);
                if (n == 1)
                {
                    yield return UnifiedStreamEvent.ToolDone(
                        "call_spawn_hung",
                        "spawn_subagent",
                        "{\"task\":\"子长任务\",\"label\":\"hung子\"}"
                    );
                    yield return UnifiedStreamEvent.Completed();
                    yield break;
                }
                // 主第二轮：子收敛后正常完成，主轮不应因子的 TIMEOUT 而报 TIMEOUT
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return UnifiedStreamEvent.Text("主轮继续完成");
                yield return UnifiedStreamEvent.Completed();
                yield break;
            }
            // 子持续 streaming：每隔一段吐一个 delta，直到自己的 Turn 超时（3s）
            while (true)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                yield return UnifiedStreamEvent.Text("子流式..");
            }
        }
    }

    /// <summary>快速 spawn：子立即给总结，主第二轮给文本，用于泄漏计数断言。</summary>
    private sealed class QuickSpawnGateway : ILlmGatewayClient
    {
        private readonly string _mainSessionId;
        private int _mainCall;

        public QuickSpawnGateway(string mainSessionId) => _mainSessionId = mainSessionId;

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            bool isMain = string.Equals(
                request.HeaderContext.SessionId,
                _mainSessionId,
                StringComparison.Ordinal
            );
            if (isMain)
            {
                int n = Interlocked.Increment(ref _mainCall);
                if (n == 1)
                {
                    yield return UnifiedStreamEvent.ToolDone(
                        "call_spawn_quick",
                        "spawn_subagent",
                        "{\"task\":\"快任务\",\"label\":\"快子\"}"
                    );
                    yield return UnifiedStreamEvent.Completed();
                    yield break;
                }
                yield return UnifiedStreamEvent.Text("主完成");
                yield return UnifiedStreamEvent.Completed();
                yield break;
            }
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("子总结：完成。");
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
