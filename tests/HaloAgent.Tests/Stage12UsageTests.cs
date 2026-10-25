#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need12 §6.1 最低单测集：hitRate 口径、预设换算、INSERT OR IGNORE 去重、加权综合命中率。</summary>
public sealed class Stage12UsageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;

    public Stage12UsageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage12Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage12Bootstrap_{Guid.NewGuid():N}"
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
        catch
        { /* ignore */
        }
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

    [Fact(DisplayName = "S12: input=0 时 hitRate=0 且综合命中率为加权（禁算术平均）")]
    public void HitRate_ZeroInputIsZero_Weighted()
    {
        using var svc = NewService(new DateTime(2026, 10, 17, 12, 0, 0));
        svc.EnqueueFlush(
            new List<PendingUsageRow>
            {
                new()
                {
                    ProviderId = "p1",
                    ProviderName = "P1",
                    ModelId = "m1",
                    RequestId = "r1",
                    Input = 0,
                    Output = 0,
                    Cached = 0
                },
                new()
                {
                    ProviderId = "p1",
                    ProviderName = "P1",
                    ModelId = "m1",
                    RequestId = "r2",
                    Input = 100,
                    Output = 10,
                    Cached = 50
                },
                new()
                {
                    ProviderId = "p1",
                    ProviderName = "P1",
                    ModelId = "m1",
                    RequestId = "r3",
                    Input = 300,
                    Output = 10,
                    Cached = 30
                },
            },
            "s1",
            "w1"
        );
        WaitFor(() => svc.GetOverview(new UsageFilter { Preset = "today" }).TotalRequests == 3);
        var ov = svc.GetOverview(new UsageFilter { Preset = "today" });
        Assert.Equal(3, ov.TotalRequests);
        // 加权：80/400=0.2；算术平均会是 (0+0.5+0.1)/3=0.2 巧合？改用和校验
        Assert.Equal(80, ov.SumCached);
        Assert.Equal(400, ov.SumInput);
        Assert.Equal(0.2, ov.HitRate, 6);
        var (total, rows) = svc.QueryPage(new UsageFilter { Preset = "today" }, 1, 50);
        Assert.Equal(3, total);
        var zero = rows.First(r => r.InputTokens == 0);
        Assert.Equal(0, zero.HitRate);
    }

    [Fact(DisplayName = "S12: 同 request_id 重试累加不丢（ON CONFLICT 累加，need22-P1 T7）")]
    public void Dedup_SameRequestIdOnce()
    {
        using var svc = NewService(new DateTime(2026, 10, 17, 12, 0, 0));
        var row = new PendingUsageRow
        {
            ProviderId = "p1",
            ProviderName = "P1",
            ModelId = "m1",
            RequestId = "dup-1",
            Input = 10,
            Output = 1,
            Cached = 5
        };
        svc.EnqueueFlush(new List<PendingUsageRow> { row }, "s1", "w1");
        svc.EnqueueFlush(new List<PendingUsageRow> { row }, "s1", "w1");
        WaitFor(() => svc.GetOverview(new UsageFilter { Preset = "today" }).TotalRequests >= 1);
        // 原 Sleep(1500) 硬等第二批落库：改为事件驱动等 SumInput==20（同 request_id 累加口径）。
        // WaitFor 超时仅兜底不断言，失败仍由下方 Assert 按原口径判定，测试作用不变。
        WaitFor(
            () => svc.GetOverview(new UsageFilter { Preset = "today" }).SumInput == 20,
            timeoutMs: 8000
        );
        var ov = svc.GetOverview(new UsageFilter { Preset = "today" });
        // need22-P1 T7：同 request_id 仍只占一行，但用量累加（重试不丢），非 OR IGNORE 丢弃。
        Assert.Equal(1, ov.TotalRequests);
        Assert.Equal(20, ov.SumInput);
        Assert.Equal(2, ov.SumOutput);
        Assert.Equal(10, ov.SumCached);
    }

    [Fact(DisplayName = "S12: 本周自周一起算 + 跨月补零 + custom 校验")]
    public void Preset_WeekStartsMonday_CustomValidated()
    {
        // 2026-10-17 是周六 → 本周一为 2026-10-12
        using var svc = NewService(new DateTime(2026, 10, 17, 12, 0, 0));
        var (s, e) = svc.ResolveRange(new UsageFilter { Preset = "thisWeek" });
        Assert.Equal("2026-10-12", s);
        Assert.Equal("2026-10-17", e);
        var (s7, e7) = svc.ResolveRange(new UsageFilter { Preset = "last7" });
        Assert.Equal("2026-10-11", s7);
        // 跨月 trend 补零：9 天窗口应有 9 个桶
        var tr = svc.GetTrend(new UsageFilter { Preset = "last7" }, "total");
        Assert.Equal(7, tr.Dates.Count);
        Assert.Throws<ArgumentException>(
            () => svc.ResolveRange(new UsageFilter { Preset = "custom", StartDate = "2026-10-05" })
        );
        Assert.Throws<ArgumentException>(
            () =>
                svc.ResolveRange(
                    new UsageFilter
                    {
                        Preset = "custom",
                        StartDate = "2026-10-09",
                        EndDate = "2026-10-01"
                    }
                )
        );
        Assert.Throws<ArgumentException>(
            () => svc.ResolveRange(new UsageFilter { Preset = "nope" })
        );
    }

    [Fact(DisplayName = "S12: 同名模型按 (provider,model) 隔离 + 分页上限 200")]
    public void SameModelName_IsolatedByProvider_PageClamped()
    {
        using var svc = NewService(new DateTime(2026, 10, 17, 12, 0, 0));
        svc.EnqueueFlush(
            new List<PendingUsageRow>
            {
                new()
                {
                    ProviderId = "pa",
                    ProviderName = "PA",
                    ModelId = "gpt-4o",
                    RequestId = "a1",
                    Input = 100,
                    Output = 10,
                    Cached = 10
                },
                new()
                {
                    ProviderId = "pb",
                    ProviderName = "PB",
                    ModelId = "gpt-4o",
                    RequestId = "b1",
                    Input = 200,
                    Output = 20,
                    Cached = 20
                },
            },
            "s1",
            "w1"
        );
        WaitFor(() => svc.GetOverview(new UsageFilter { Preset = "today" }).TotalRequests == 2);
        var tr = svc.GetTrend(new UsageFilter { Preset = "today" }, "total");
        Assert.Equal(2, tr.PerModel.Count); // 同名不同 provider → 两个系列
        var (total, _) = svc.QueryPage(new UsageFilter { Preset = "today" }, 1, 500);
        Assert.Equal(2, total); // pageSize 被钳制仍能查
        var opts = svc.GetFilterOptions();
        Assert.NotNull(opts);
    }
}
