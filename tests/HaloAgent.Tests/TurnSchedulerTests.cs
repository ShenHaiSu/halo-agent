#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Services.Agent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need21-B03: 流水线新增测试（mock IAgentEventSink，不起 Cef/WinForms）。
/// 调度器终端分支 + BudgetGuard 纯函数 + TurnContext 计数 + Core 归属。
/// </summary>
public sealed class TurnSchedulerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public TurnSchedulerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloNeed21B03_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloNeed21B03Boot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch { }
        try
        {
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, true);
        }
        catch { }
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        public readonly List<(string Session, string Kind)> Events = new();

        public Task EmitAsync(string sessionId, string kind, object? payload)
        {
            lock (Events)
                Events.Add((sessionId, kind));
            return Task.CompletedTask;
        }

        public void EmitStreamDelta(string sessionId, string kind, string textDelta)
        {
            lock (Events)
                Events.Add((sessionId, kind + ":delta"));
        }
    }

    private sealed class ThrowingGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("need21-B03: 终端分支不得触达网关");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private AgentOrchestrator NewOrch(IAgentEventSink sink) =>
        new(
            _service,
            _budgetService,
            new ThrowingGateway(),
            new ToolRegistry(),
            _storageManager,
            sink: sink
        );

    [Fact]
    public async Task Scheduler_CancelledBeforeStart_NoGateway_NoCef()
    {
        var sink = new RecordingSink();
        var orch = NewOrch(sink);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await orch.RunAgentTurnAsync("ws", "ss", "hi", cancellationToken: cts.Token);
        Assert.False(r.Success);
        Assert.True(r.WasCancelled);
        Assert.Contains(sink.Events, e => e.Kind == "TurnCancelled");
    }

    [Fact]
    public async Task Scheduler_EmptyPrompt_FailsWithoutGateway()
    {
        var sink = new RecordingSink();
        var orch = NewOrch(sink);
        var ws = await _service.AddOrUpdateWorkspaceAsync(Path.Combine(_tempRoot, "P"), "P");
        var ss = await _service.CreateSessionAsync(ws.Id, "s");
        var r = await orch.RunAgentTurnAsync(ws.Id, ss.Id, "   ");
        Assert.False(r.Success);
        Assert.Equal("EMPTY_PROMPT", r.ErrorCode);
        Assert.DoesNotContain(sink.Events, e => e.Kind == "IterationStarted");
    }

    [Fact]
    public async Task Scheduler_InvalidSession_FailsWithoutGateway()
    {
        var orch = NewOrch(new RecordingSink());
        var r = await orch.RunAgentTurnAsync("", "", "hi");
        Assert.False(r.Success);
        Assert.Equal("INVALID_SESSION", r.ErrorCode);
    }

    [Fact]
    public void AgentTurnResult_LivesInCoreAssembly()
    {
        Assert.Equal("HaloAgent.Core", typeof(AgentTurnResult).Assembly.GetName().Name);
    }

    [Fact]
    public void Scheduler_UsesInjectedSink_NotNull()
    {
        var sink = new RecordingSink();
        var orch = NewOrch(sink);
        Assert.NotNull(orch);
        Assert.Equal(25, orch.MaxIterations);
    }
}

/// <summary>need21-B03: BudgetGuard 纯函数测试（无外部依赖）。</summary>
public sealed class BudgetGuardTests
{
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(150, 100, true)]
    [InlineData(99, 100, false)]
    [InlineData(50, 0, false)]
    public void ShouldTruncate(int used, int budget, bool expected) =>
        Assert.Equal(expected, BudgetGuard.ShouldTruncate(used, budget));

    [Theory]
    [InlineData(92, 100, true)]
    [InlineData(91, 100, false)]
    [InlineData(0, 0, false)]
    public void IsHighWater(int used, int budget, bool expected) =>
        Assert.Equal(expected, BudgetGuard.IsHighWater(used, budget));

    [Fact]
    public void RepeatedError_TripsAtThree()
    {
        Assert.False(BudgetGuard.ShouldStopOnRepeatedError(2));
        Assert.True(BudgetGuard.ShouldStopOnRepeatedError(3));
        Assert.True(BudgetGuard.ShouldStopOnRepeatedError(5, 5));
        Assert.False(BudgetGuard.ShouldStopOnRepeatedError(4, 5));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void IsInfinite(int max, bool expected) =>
        Assert.Equal(expected, BudgetGuard.IsInfinite(max));

    [Fact]
    public void TurnContext_NoteErrorCountsConsecutively()
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "w",
            SessionId = "s",
            UserText = "u"
        };
        Assert.False(ctx.NoteToolError("E1"));
        Assert.False(ctx.NoteToolError("E2"));
        Assert.False(ctx.NoteToolError("E2"));
        Assert.True(ctx.NoteToolError("E2"));
        ctx.NoteToolSuccess();
        Assert.Equal(0, ctx.ConsecutiveToolErrors);
        Assert.False(ctx.NoteToolError("E2"));
    }
}
