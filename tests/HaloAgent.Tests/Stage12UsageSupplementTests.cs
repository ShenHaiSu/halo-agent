#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

/// <summary>need12-P0/P1/P2：取消与旁路用量补记回归（永不抛、幂等、估算口径）。</summary>
public sealed class Stage12UsageSupplementTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;

    public Stage12UsageSupplementTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage12Sup_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage12SupBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
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

    private UsageMetricsService NewService(DateTime? now = null)
    {
        var svc = new UsageMetricsService(_storage);
        if (now.HasValue)
            svc.NowLocal = () => now.Value;
        return svc;
    }

    private static void WaitFor(Func<bool> cond, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
                return;
            Thread.Sleep(20);
        }
    }

    private static (ProviderConfig, ModelConfig) FakeProviderModel(
        string pid = "p1",
        string mid = "m1"
    )
    {
        var provider = new ProviderConfig
        {
            Id = pid,
            Name = "P1",
            BaseUrl = "https://api.openai.com/v1"
        };
        var model = new ModelConfig
        {
            Id = mid,
            DisplayName = mid,
            Protocol = LlmProtocolType.Chat
        };
        provider.Models.Add(model);
        return (provider, model);
    }

    [Fact(DisplayName = "P2: source+estimated 落库且可筛选；老字段默认 agent/0")]
    public void SourceEstimated_RoundTrip_And_Filter()
    {
        using var svc = NewService(new DateTime(2026, 10, 18, 12, 0, 0));
        var (provider, model) = FakeProviderModel();
        var rows = new List<PendingUsageRow>();
        UsageRecorder.Collect(
            rows,
            provider,
            model,
            new UnifiedTokenUsage(100, 20, 10, 120),
            "ok",
            "req-agent-1",
            "s1",
            "w1",
            UsageRecorder.SourceAgent
        );
        UsageRecorder.CollectAuto(
            rows,
            provider,
            model,
            null,
            500,
            60,
            "cancelled",
            "req-compact-1",
            "s1",
            "w1",
            UsageRecorder.SourceCompact
        );
        svc.EnqueueFlush(rows, "s1", "w1");
        WaitFor(() => svc.GetOverview(new UsageFilter { Preset = "today" }).TotalRequests == 2);
        var (total, all) = svc.QueryPage(new UsageFilter { Preset = "today" }, 1, 50);
        Assert.Equal(2, total);
        var agent = all.First(r => r.RequestDatetime != null && all.Any(x => x.Source == "agent"));
        Assert.Equal("agent", all.First(r => r.Source == "agent").Source);
        Assert.Equal(0, all.First(r => r.Source == "agent").Estimated);
        var est = all.First(r => r.Source == "compact");
        Assert.Equal(1, est.Estimated);
        Assert.Equal(500, est.InputTokens);
        Assert.Equal(60, est.OutputTokens);
        // 来源筛选
        var (ct, _) = svc.QueryPage(
            new UsageFilter
            {
                Preset = "today",
                Sources = new List<string> { "compact" }
            },
            1,
            50
        );
        Assert.Equal(1, ct);
        var (et, _) = svc.QueryPage(
            new UsageFilter { Preset = "today", EstimatedOnly = true },
            1,
            50
        );
        Assert.Equal(1, et);
        var (mt, _) = svc.QueryPage(
            new UsageFilter { Preset = "today", EstimatedOnly = false },
            1,
            50
        );
        Assert.Equal(1, mt);
        var opts = svc.GetFilterOptions();
        Assert.NotNull(opts);
    }

    [Fact(DisplayName = "P0: Agent 流内抛取消仍保住前面 iteration")]
    public async Task Agent_CancelMidStream_KeepsPriorIterations()
    {
        var repository = new SessionRepository(_storage);
        var service = new WorkspaceSessionService(repository);
        var budget = new TokenBudgetService();
        string proj = Path.Combine(_tempRoot, "Proj");
        Directory.CreateDirectory(proj);
        var ws = await service.AddOrUpdateWorkspaceAsync(proj, "W");
        var sess = await service.CreateSessionAsync(ws.Id, "S");
        using var svc = NewService(new DateTime(2026, 10, 18, 12, 0, 0));

        var gateway = new TwoTurnThenCancelGateway();
        var orch = new AgentOrchestrator(
            service,
            budget,
            gateway,
            new ToolRegistry(registerBuiltIns: false),
            _storage,
            usageMetrics: svc
        );
        var result = await orch.RunAgentTurnAsync(ws.Id, sess.Id, "hi");
        Assert.True(result.WasCancelled);
        WaitFor(() => svc.GetOverview(new UsageFilter { Preset = "today" }).TotalRequests >= 1);
        var ov = svc.GetOverview(new UsageFilter { Preset = "today" });
        // 第 1 轮 ok（实测 100/20）+ 第 2 轮 cancelled（估算 input>0）
        Assert.True(ov.TotalRequests >= 2);
        Assert.True(ov.SumInput >= 100);
        var (_, rows) = svc.QueryPage(new UsageFilter { Preset = "today" }, 1, 50);
        Assert.Contains(rows, r => r.Status == "ok" && r.Estimated == 0);
        Assert.Contains(
            rows,
            r => r.Status == "cancelled" && r.Estimated == 1 && r.InputTokens > 0
        );
    }

    [Fact(DisplayName = "P2: 估算永不覆盖实测；全 0 仍记 cancelled 计数")]
    public void CollectAuto_PrefersMeasured_AllZeroStillCounts()
    {
        var (provider, model) = FakeProviderModel();
        var rows = new List<PendingUsageRow>();
        UsageRecorder.CollectAuto(
            rows,
            provider,
            model,
            new UnifiedTokenUsage(10, 2, 0, 12),
            9999,
            9999,
            "cancelled",
            "r-measured",
            "s",
            "w",
            UsageRecorder.SourceAgent
        );
        UsageRecorder.CollectAuto(
            rows,
            provider,
            model,
            null,
            0,
            0,
            "cancelled",
            "r-zero",
            "s",
            "w",
            UsageRecorder.SourceAgent
        );
        Assert.Equal(10, rows[0].Input);
        Assert.Equal(2, rows[0].Output);
        Assert.Equal(0, rows[0].Estimated);
        Assert.Equal(0, rows[1].Input);
        Assert.Equal(1, rows[1].Estimated);
    }

    private sealed class TwoTurnThenCancelGateway : ILlmGatewayClient
    {
        private int _calls;

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            _calls++;
            if (_calls == 1)
            {
                // 第一轮带工具调用，保证进入第 2 轮；实测用量 100/20。
                yield return UnifiedStreamEvent.ToolDone("call_1", "list_dir", "{\"path\":\".\"}");
                yield return UnifiedStreamEvent.Tokens(100, 20, 10, 120);
                yield return UnifiedStreamEvent.Completed();
                yield break;
            }
            yield return UnifiedStreamEvent.Text("partial-");
            await Task.Yield();
            // 第二轮流中抛取消：模拟用户手动中断（不带 ct，避免被当成外部取消）。
            throw new OperationCanceledException("user-cancel");
        }
    }
}
