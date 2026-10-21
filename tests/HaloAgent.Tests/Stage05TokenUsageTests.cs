#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need05-01：账单 vs 占用分离 + 未知态 + 多 call 累计（至少 6 个）。</summary>
public sealed class Stage05TokenUsageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public Stage05TokenUsageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage05Usage_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage05UsageBoot_{Guid.NewGuid():N}"
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

    private static UnifiedChatMessage AssistantWithSnapshot(
        int input,
        int output = 100,
        int cached = 0
    )
    {
        var m = UnifiedChatMessage.Assistant("reply");
        m.InputTokens = input;
        m.OutputTokens = output;
        m.CachedTokens = cached;
        return m;
    }

    [Fact(DisplayName = "S05-01: 占用取末轮快照而非 Sum")]
    public void Occupancy_Uses_Last_Snapshot_Not_Sum()
    {
        var messages = new List<UnifiedChatMessage>
        {
            AssistantWithSnapshot(48083),
            AssistantWithSnapshot(48083),
            AssistantWithSnapshot(48083),
        };
        var (prompt, cached, isEst) = TokenBudgetService.ResolveOccupancy(messages);
        Assert.Equal(48083, prompt);
        Assert.False(isEst);

        var model = new ModelConfig
        {
            Id = "m",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096
        };
        var status = _budgetService.CalculateBudgetStatus(model, messages);
        Assert.Equal(48083, status.UsedInputTokens);
        Assert.Equal("known", status.OccupancyState);
        Assert.False(status.UsedInputIsEstimated);
        Assert.Equal(48083, status.LastPromptTokens);
    }

    [Fact(DisplayName = "S05-01: 无快照时为 unknown + 估算回退")]
    public void Occupancy_Unknown_When_No_Snapshot()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hello"),
            UnifiedChatMessage.Assistant("hi"),
        };
        var (prompt, cached, isEst) = TokenBudgetService.ResolveOccupancy(messages);
        Assert.Null(prompt);
        Assert.Null(cached);
        Assert.True(isEst);

        var model = new ModelConfig
        {
            Id = "m",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096
        };
        var status = _budgetService.CalculateBudgetStatus(model, messages);
        Assert.Null(status.LastPromptTokens);
        Assert.Null(status.LastCachedTokens);
        Assert.Null(status.CacheHitRate);
        Assert.Equal("unknown", status.OccupancyState);
        Assert.True(status.UsedInputIsEstimated);
        Assert.True(status.UsedInputTokens > 0); // 兼容旧字段：估算回退
    }

    [Fact(DisplayName = "S05-01: cached 钳制 ≤ input，命中率恒 ≤1")]
    public void Cached_Clamped_To_Input()
    {
        var m = AssistantWithSnapshot(48083, 100, 90000);
        var (prompt, cached, _) = TokenBudgetService.ResolveOccupancy(
            new List<UnifiedChatMessage> { m }
        );
        Assert.Equal(48083, prompt);
        Assert.Equal(48083, cached);

        var model = new ModelConfig
        {
            Id = "m",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096
        };
        var status = _budgetService.CalculateBudgetStatus(
            model,
            new List<UnifiedChatMessage> { m }
        );
        Assert.Equal(48083, status.LastCachedTokens);
        Assert.Equal(1.0, status.CacheHitRate);
    }

    [Fact(DisplayName = "S05-01: 账单累计 Σ，占用保持快照")]
    public async Task Billing_Accumulates_While_Occupancy_Snapshots()
    {
        string proj = Path.Combine(_tempRoot, "Proj");
        Directory.CreateDirectory(proj);
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "计费项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "计费会话");

        foreach (int input in new[] { 48000, 49000, 50000 })
            await _service.AppendSessionMessageAsync(
                ws.Id,
                sess.Id,
                AssistantWithSnapshot(input, 100, 1000)
            );

        var meta = await _service.GetSessionAsync(ws.Id, sess.Id);
        Assert.NotNull(meta);
        Assert.Equal(50000, meta!.TotalInputTokens); // 占用=末轮快照
        Assert.Equal(48000 + 49000 + 50000, meta.CumulativePromptTokens); // Σ 累计
        Assert.Equal(300, meta.CumulativeCompletionTokens);
        Assert.Equal(300, meta.TotalOutputTokens); // TotalOutput 保持 +=（Σ）
    }

    [Fact(DisplayName = "S05-01: 空会话为 known + 0%")]
    public void Empty_Session_Is_Known_Zero()
    {
        var model = new ModelConfig
        {
            Id = "m",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096
        };
        var status = _budgetService.CalculateBudgetStatus(model, new List<UnifiedChatMessage>());
        Assert.Equal("known", status.OccupancyState);
        Assert.Equal(0, status.UsedInputTokens);
        Assert.False(status.UsedInputIsEstimated);
    }

    [Fact(DisplayName = "S05-01: 多 call 轮 billed=Σ，input=末轮占用")]
    public async Task Turn_Billing_Sums_MultiCall()
    {
        string proj = Path.Combine(_tempRoot, "ProjMulti");
        Directory.CreateDirectory(proj);
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "多 call 项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "多 call 会话");

        var gateway = new ScriptedUsageGateway(
            new Queue<List<UnifiedStreamEvent>>(
                new[]
                {
                    // 第 1 call：工具调用轮
                    new List<UnifiedStreamEvent>
                    {
                        UnifiedStreamEvent.ToolDone("call_1", "list_dir", "{\"path\":\".\"}"),
                        UnifiedStreamEvent.Tokens(48000, 100, 1000, 48100),
                        UnifiedStreamEvent.Completed(),
                    },
                    // 第 2 call：收尾轮
                    new List<UnifiedStreamEvent>
                    {
                        UnifiedStreamEvent.Text("done"),
                        UnifiedStreamEvent.Tokens(49000, 200, 2000, 49200),
                        UnifiedStreamEvent.Completed(),
                    },
                }
            )
        );
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(registerBuiltIns: false),
            _storageManager
        );

        var result = await orchestrator.RunAgentTurnAsync(ws.Id, sess.Id, "列目录后总结");
        Assert.True(result.Success);

        var meta = await _service.GetSessionAsync(ws.Id, sess.Id);
        Assert.NotNull(meta);
        Assert.Equal(49000, meta!.TotalInputTokens); // 末 call 占用
        Assert.Equal(48000 + 49000, meta.CumulativePromptTokens); // billed Σ
        Assert.Equal(100 + 200, meta.CumulativeCompletionTokens);
    }

    private sealed class ScriptedUsageGateway : ILlmGatewayClient
    {
        private readonly Queue<List<UnifiedStreamEvent>> _script;

        public ScriptedUsageGateway(Queue<List<UnifiedStreamEvent>> script) => _script = script;

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            var events =
                _script.Count > 0
                    ? _script.Dequeue()
                    : new List<UnifiedStreamEvent>
                    {
                        UnifiedStreamEvent.Text("x"),
                        UnifiedStreamEvent.Completed()
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
