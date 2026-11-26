#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
/// 子代理轮次上限与截断状态回归：spawn 参数不被全局配置覆盖；
/// 截断结果在子会话元数据与 Barrier 回包里语义一致。
/// </summary>
public sealed class SubAgentIterationLimitTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly AgentConfigService _agentService;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public SubAgentIterationLimitTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentIterLimit_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentIterLimitBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _agentService = new AgentConfigService(_storageManager);
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
        _projectDir = Path.Combine(_tempRoot, "Projects", "IterationLimit");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "轮次上限测试")
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

    private AgentConfigDocument Config(int globalMax) =>
        new()
        {
            ExecDefaultTimeoutMs = 30000,
            ExecMaxTimeoutMs = 300000,
            MaxIterations = globalMax,
            SubAgentEnabled = true,
            SubMaxParallel = 2,
            SubMaxIterations = 15,
        };

    /// <summary>
    /// 起一个持续调用工具的子跑到步数耗尽，返回子会话元数据、Barrier 结果与网关调用次数。
    /// 全局上限与 spawn 上限分离，用于验证全局配置不覆盖子实例自己的预算。
    /// </summary>
    private async Task<(
        SessionMetadata Sub,
        SubAgentResult Result,
        int GatewayCalls
    )> RunOneSubAsync(int globalMax, int specMax)
    {
        _agentService.SaveAgentConfig(Config(globalMax));
        try
        {
            var main = await _service.CreateSessionAsync(_workspace.Id, "sess-main");
            await File.WriteAllTextAsync(Path.Combine(_projectDir, "notes.txt"), "line1-content\n");
            var gateway = new LoopingToolGateway("read_file", "{\"file_path\":\"notes.txt\"}");
            var coord = new SubAgentCoordinator(
                _service,
                _budgetService,
                gateway,
                _storageManager,
                null,
                null,
                _agentService,
                null
            );
            var policy = new SubPolicySnapshot(_agentService.GetInternal());
            var spec = new SpawnCallSpec
            {
                CallId = "call-sub",
                Task = "持续列目录直到步数耗尽",
                Label = "sess-sub",
                MaxIterations = specMax,
            };
            var (handles, rejected) = await coord.CreateSubsAsync(
                _workspace.Id,
                main.Id,
                new[] { spec },
                string.Empty,
                string.Empty,
                policy,
                CancellationToken.None
            );
            Assert.Empty(rejected);
            var results = await coord.BarrierAsync(
                _workspace.Id,
                main.Id,
                handles,
                rejected,
                CancellationToken.None,
                TimeSpan.FromSeconds(30)
            );
            var sub = await _service.GetSessionAsync(_workspace.Id, handles[0].SubSessionId);
            Assert.NotNull(sub);
            Assert.Single(results);
            return (sub!, results[0], gateway.CallCount);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "spawn 传入的轮次上限覆盖全局配置")]
    public async Task SpawnMaxIterationsOverridesGlobalConfig()
    {
        var (_, result, callCount) = await RunOneSubAsync(globalMax: 25, specMax: 3);

        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, callCount);
        Assert.Equal("TURN_TRUNCATED", result.ErrorCode);
    }

    [Fact(DisplayName = "步数耗尽的子会话元数据标记为 truncated")]
    public async Task SubAgentTruncatedStatusPersisted()
    {
        var (sub, _, _) = await RunOneSubAsync(globalMax: 25, specMax: 3);

        Assert.Equal("truncated", sub.SubStatus);
    }

    [Fact(DisplayName = "截断子在 Barrier 回包里仍是失败且状态与元数据一致")]
    public async Task TruncatedSubResultStillFailureInBarrier()
    {
        var (sub, result, _) = await RunOneSubAsync(globalMax: 25, specMax: 3);

        Assert.False(result.Success);
        Assert.Equal("TURN_TRUNCATED", result.ErrorCode);
        Assert.Equal("truncated", sub.SubStatus);
    }

    [Fact(DisplayName = "主轮仍遵循全局轮次上限")]
    public async Task MainTurnStillHonorsGlobalConfig()
    {
        _agentService.SaveAgentConfig(Config(4));
        try
        {
            var session = await _service.CreateSessionAsync(_workspace.Id, "sess-main-config");
            await File.WriteAllTextAsync(Path.Combine(_projectDir, "notes.txt"), "line1-content\n");
            var gateway = new LoopingToolGateway("read_file", "{\"file_path\":\"notes.txt\"}");
            var orchestrator = new AgentOrchestrator(
                _service,
                _budgetService,
                gateway,
                new ToolRegistry(),
                _storageManager,
                agentConfigService: _agentService
            );

            var result = await orchestrator.RunAgentTurnAsync(
                _workspace.Id,
                session.Id,
                "持续列目录直到步数耗尽"
            );

            Assert.Equal(4, _agentService.GetInternal().MaxIterations);
            Assert.Equal(4, orchestrator.ResolvedMaxIterations);
            Assert.True(result.WasTruncated);
            Assert.Equal(4, result.Iterations);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    /// <summary>每轮返回同一个工具调用，逼 ReAct 循环跑到轮次上限截断；不连真模型。</summary>
    private sealed class LoopingToolGateway : ILlmGatewayClient
    {
        private readonly string _toolName;
        private readonly string _argsJson;
        private int _count;
        private int _seq;

        public LoopingToolGateway(
            string toolName = "list_dir",
            string argsJson = "{\"path\":\".\"}"
        )
        {
            _toolName = toolName;
            _argsJson = argsJson;
        }

        public int CallCount => Volatile.Read(ref _count);

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _count);
            var events = new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.ToolDone(
                    $"call_loop_{Interlocked.Increment(ref _seq):N}",
                    _toolName,
                    _argsJson
                ),
                UnifiedStreamEvent.Completed(),
            };
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
