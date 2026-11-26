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
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// Barrier 预算联动主轮超时的回归：有限超时显著提前触发、无限模式保持原口径、
/// 超时子仍按截断结果回填且主轮未被取消。
/// </summary>
public sealed class SubAgentBarrierBudgetTests : IDisposable
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

    public SubAgentBarrierBudgetTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentBarrierBudget_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentBarrierBudgetBoot_{Guid.NewGuid():N}"
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "BarrierBudget");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "预算联动测试")
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

    private AgentConfigDocument Config(int turnTimeoutMs) =>
        new()
        {
            ExecDefaultTimeoutMs = 30000,
            ExecMaxTimeoutMs = 300000,
            MaxIterations = 25,
            TurnTimeoutMs = turnTimeoutMs,
            SubBarrierBufferMs = 30000,
            SubAgentEnabled = true,
            SubMaxParallel = 2,
        };

    private SubAgentCoordinator NewCoordinator() =>
        new(_service, _budgetService, new StubGateway(), agentConfig: _agentService);

    [Fact(DisplayName = "有限主轮超时下 Barrier 预算显著早于主轮超时")]
    public void BarrierBudget_EarlierThanMainTurnTimeout()
    {
        _agentService.SaveAgentConfig(Config(60000));
        try
        {
            double budgetMs = NewCoordinator().ResolveBarrierBudget(2).TotalMilliseconds;

            Assert.InRange(budgetMs, 1000, 59999);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "无限时间模式下 Barrier 预算保持子数乘默认单轮超时加余量")]
    public void BarrierBudget_InfiniteMode_Unchanged()
    {
        _agentService.SaveAgentConfig(Config(0));
        try
        {
            double budgetMs = NewCoordinator().ResolveBarrierBudget(2).TotalMilliseconds;
            long expected = Math.Clamp(
                (long)2 * AgentDefaults.TurnTimeoutMs + AgentDefaults.SubBarrierBufferMs,
                1000L,
                (long)AgentDefaults.HardTimeoutCeilMs * 8
            );

            Assert.Equal(expected, budgetMs);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "Barrier 超时仍返回截断结果且主轮未被取消")]
    public async Task BarrierTimeout_StillReturnsTruncatedResults()
    {
        using var childCts = new CancellationTokenSource();
        using var mainCts = new CancellationTokenSource();
        var slowTask = Task.Run(
            async () =>
            {
                await Task.Delay(Timeout.Infinite, childCts.Token);
                return AgentTurnResult.Cancelled(0, 0);
            },
            childCts.Token
        );
        var handle = new SubHandle
        {
            CallId = "call-slow",
            SubSessionId = "sess-slow",
            Label = "sess-slow",
            Task = slowTask,
            Cts = childCts,
        };
        var coord = NewCoordinator();

        var results = await coord.BarrierAsync(
            _workspace.Id,
            "sess-main",
            new[] { handle },
            Array.Empty<(string, string)>(),
            mainCts.Token,
            TimeSpan.FromMilliseconds(50)
        );

        Assert.False(mainCts.IsCancellationRequested);
        var result = Assert.Single(results);
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Equal("SUB_TIMEOUT", result.ErrorCode);
        Assert.Contains(
            "SUBAGENT_BARRIER_TIMEOUT",
            coord.GetRecentRejections().Select(r => r.Code)
        );
    }

    /// <summary>占位网关：预算计算与 Barrier 回填都不走模型，仅需满足构造契约。</summary>
    private sealed class StubGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
