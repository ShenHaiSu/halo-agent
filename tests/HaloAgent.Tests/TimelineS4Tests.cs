#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 发货加固：翻转事务 + 终态落盘 + 完整性 + 长轮性能 + 顺序一致 + 懒检阈值。
/// </summary>
public sealed class TimelineS4Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryStore _store;

    public TimelineS4Tests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS4_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS4Boot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _store = new SessionHistoryStore(_storageManager);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        try
        {
            Directory.Delete(_bootstrapRoot, true);
        }
        catch { }
    }

    private static TurnSummary MakeTurn(
        string turnId,
        string status,
        string? finalBlockId,
        int blocks = 1,
        int tools = 0
    ) =>
        new()
        {
            TurnId = turnId,
            TurnSeq = 0,
            IterEnd = 1,
            UserPreview = "u",
            FinalBlockId = finalBlockId,
            FinalPreview = finalBlockId == null ? string.Empty : "final",
            Blocks = blocks,
            Tools = tools,
            Status = status,
            LastRequestId = "req-s4",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        };

    private static TimelineBlock MakeTextBlock(
        string blockId,
        string turnId,
        int iter,
        long seq,
        string tag
    ) =>
        new()
        {
            BlockId = blockId,
            TurnId = turnId,
            Iteration = iter,
            Sequence = seq,
            Kind = TimelineBlockKind.Text,
            Tag = tag,
            CharCount = 5,
            Preview = "hello",
            Content = "hello body"
        };

    [Fact]
    public async Task SecondIterationAppendFlipsStaleFinalAtomically()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4flip";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                MakeTextBlock("f:iter1", turn, 1, 1, TimelineBlockTag.Final)
            },
            MakeTurn(turn, TimelineTurnStatus.Completed, "f:iter1")
        );
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                MakeTextBlock("f:iter2", turn, 2, 2, TimelineBlockTag.Final)
            },
            new TurnSummary
            {
                TurnId = turn,
                TurnSeq = 0,
                IterEnd = 2,
                UserPreview = "u",
                FinalBlockId = "f:iter2",
                FinalPreview = "final",
                Blocks = 1,
                Status = TimelineTurnStatus.Completed,
                LastRequestId = "req-s4",
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            }
        );
        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "f:iter1", "f:iter2" }
        );
        Assert.Empty(missing);
        Assert.Equal(TimelineBlockTag.Process, items["f:iter1"].Tag);
        Assert.Equal(TimelineBlockTag.Final, items["f:iter2"].Tag);
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        var hit = Assert.Single(summaries);
        Assert.Equal("f:iter2", hit.FinalBlockId);
        // 终态唯一：同轮仅一个 final
        Assert.Equal(1, items.Values.Count(v => v.Tag == TimelineBlockTag.Final));
    }

    [Fact]
    public async Task ExplicitFlipReturnsIdsAndClearsFinalRef()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4explicit";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock> { MakeTextBlock("x:old", turn, 1, 1, TimelineBlockTag.Final) },
            MakeTurn(turn, TimelineTurnStatus.Completed, "x:old")
        );
        var flipped = await _store.FlipFinalToProcessAsync(ws, sess, turn, 2);
        Assert.Contains("x:old", flipped);
        var (items, _) = await _store.GetBlockContentsAsync(ws, sess, new[] { "x:old" });
        Assert.Equal(TimelineBlockTag.Process, items["x:old"].Tag);
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Null(Assert.Single(summaries).FinalBlockId);
        // 同 iter 不翻转
        var none = await _store.FlipFinalToProcessAsync(ws, sess, turn, 1);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(TimelineTurnStatus.Truncated)]
    [InlineData(TimelineTurnStatus.Cancelled)]
    [InlineData(TimelineTurnStatus.Error)]
    public async Task MarkEndedDemotesFinalAndPersistsBadge(string status)
    {
        string sess = "sess-s4mark-" + status;
        const string ws = "ws-s4";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                MakeTextBlock("m:final", turn, 1, 1, TimelineBlockTag.Final)
            },
            MakeTurn(turn, TimelineTurnStatus.Completed, "m:final")
        );
        Assert.True(await _store.MarkTurnEndedAsync(ws, sess, turn, status));
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        var hit = Assert.Single(summaries);
        Assert.Equal(status, hit.Status);
        Assert.Null(hit.FinalBlockId);
        var (items, _) = await _store.GetBlockContentsAsync(ws, sess, new[] { "m:final" });
        Assert.Equal(TimelineBlockTag.Process, items["m:final"].Tag);
    }

    [Fact]
    public async Task MarkEndedOnMissingTurnReturnsFalse()
    {
        Assert.False(
            await _store.MarkTurnEndedAsync(
                "ws-s4",
                "sess-s4missing",
                "uturn-absent",
                TimelineTurnStatus.Cancelled
            )
        );
    }

    [Fact]
    public async Task AllBadgesRoundtripThroughSummary()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4badges";
        string[] states =
        {
            TimelineTurnStatus.Completed,
            TimelineTurnStatus.Truncated,
            TimelineTurnStatus.Cancelled,
            TimelineTurnStatus.Error
        };
        for (int i = 0; i < states.Length; i++)
        {
            string turn = "uturn-badge" + i;
            await _store.AppendTurnAsync(
                ws,
                sess,
                new List<UnifiedChatMessage>(),
                new List<TimelineBlock>
                {
                    MakeTextBlock("g:b" + i, turn, 1, i + 1, TimelineBlockTag.Process)
                },
                MakeTurn(turn, states[i], null)
            );
        }
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Equal(states, summaries.Select(s => s.Status).ToArray());
    }

    [Theory]
    [InlineData(TimelineTurnStatus.Completed, 1)]
    [InlineData(TimelineTurnStatus.Truncated, 0)]
    [InlineData(TimelineTurnStatus.Cancelled, 0)]
    [InlineData(TimelineTurnStatus.Error, 0)]
    public void FinalizerVectorOnlyCompletedProducesFinal(string status, int expectedFinals)
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "ws-s4",
            SessionId = "sess-s4vector",
            UserText = "q",
            RequestId = "req-vector"
        };
        ctx.UserMessage = UnifiedChatMessage.User("q");
        var textDraft = new TimelineBlockDraft
        {
            BlockId = "req-vector:text1",
            Kind = TimelineBlockKind.Text,
            Iteration = 1,
            Sequence = 1
        };
        textDraft.Text.Append("hello answer");
        var toolDraft = new TimelineBlockDraft
        {
            BlockId = "req-vector:tool1",
            Kind = TimelineBlockKind.ToolCall,
            CallId = "call-1",
            Iteration = 1,
            Sequence = 2
        };
        toolDraft.Args.Append("{\"a\":1}");
        ctx.TimelineLog.Add(textDraft);
        ctx.TimelineLog.Add(toolDraft);
        var (blocks, turn) = TimelineFinalizer.Finalize(ctx, 1, status);
        Assert.Equal(expectedFinals, blocks.Count(b => b.Tag == TimelineBlockTag.Final));
        Assert.All(blocks, b => Assert.NotEqual(TimelineBlockTag.Pending, b.Tag));
        Assert.Equal(status, turn.Status);
        if (expectedFinals == 0)
        {
            Assert.Null(turn.FinalBlockId);
        }
        else
        {
            Assert.NotNull(turn.FinalBlockId);
        }
    }

    [Fact]
    public async Task IntegrityDetectsGroupMismatchAndCountsIt()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4mismatch";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock> { MakeTextBlock("q:b1", turn, 1, 1, TimelineBlockTag.Final) },
            MakeTurn(turn, TimelineTurnStatus.Completed, "q:b1", blocks: 5)
        );
        long before = TimelineObservability.Snapshot().IntegrityFailCount;
        var (ok, detail) = await _store.CheckIntegrityAsync(ws, sess);
        Assert.False(ok);
        Assert.StartsWith("group-mismatch:", detail);
        Assert.Equal(before + 1, TimelineObservability.Snapshot().IntegrityFailCount);
    }

    [Fact]
    public async Task CheckOnceSkipsMissingDbWithoutCreatingFile()
    {
        const string ws = "ws-s4";
        string sess = "sess-s4-nodb-" + Guid.NewGuid().ToString("N");
        SessionHistoryStore.ClearCheckCacheForTests();
        Assert.True(await _store.CheckOnceAsync(ws, sess));
        Assert.False(File.Exists(_storageManager.GetHistoryDbPath(ws, sess)));
        // 第二次命中进程内缓存
        Assert.True(await _store.CheckOnceAsync(ws, sess));
    }

    [Fact]
    public void StartupScanDefersOnlyBeyondThreshold()
    {
        Assert.False(HostBridgeFacade.ShouldDeferStartupScan(0));
        Assert.False(
            HostBridgeFacade.ShouldDeferStartupScan(HostBridgeFacade.StartupScanLazyThreshold)
        );
        Assert.True(
            HostBridgeFacade.ShouldDeferStartupScan(HostBridgeFacade.StartupScanLazyThreshold + 1)
        );
    }

    [Fact]
    public async Task StreamOrderMatchesSummaryOrder()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4order";
        for (int t = 1; t <= 3; t++)
        {
            string turn = "uturn-order" + t;
            await _store.AppendTurnAsync(
                ws,
                sess,
                new List<UnifiedChatMessage>(),
                new List<TimelineBlock>
                {
                    MakeTextBlock($"o:{t}a", turn, 1, t * 10 + 1, TimelineBlockTag.Process),
                    MakeTextBlock($"o:{t}b", turn, 1, t * 10 + 2, TimelineBlockTag.Final)
                },
                MakeTurn(turn, TimelineTurnStatus.Completed, $"o:{t}b", blocks: 2)
            );
        }
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Equal(3, summaries.Count);
        for (int i = 1; i < summaries.Count; i++)
        {
            Assert.True(summaries[i].TurnSeq > summaries[i - 1].TurnSeq);
        }
        foreach (var summary in summaries)
        {
            var ids = await _store.GetBlockIdsByTurnAsync(ws, sess, summary.TurnId);
            Assert.Equal(2, ids.Count);
            var (items, _) = await _store.GetBlockContentsAsync(ws, sess, ids);
            long prev = long.MinValue;
            foreach (string id in ids)
            {
                long seq = items[id].Seq;
                Assert.True(seq > prev);
                prev = seq;
            }
        }
    }

    [Fact]
    public async Task LongTurnSummaryAndPointQueryWithinBudget()
    {
        const string ws = "ws-s4";
        const string sess = "sess-s4long";
        const string turn = "uturn-long1";
        var blocks = new List<TimelineBlock>(1000);
        for (int i = 1; i <= 1000; i++)
        {
            string kind = TimelineBlockKind.Text;
            string tag = TimelineBlockTag.Process;
            if (i % 16 == 0)
            {
                kind = TimelineBlockKind.ToolCall;
            }
            else if (i % 16 == 8)
            {
                kind = TimelineBlockKind.ToolResult;
            }
            if (i == 1000)
            {
                tag = TimelineBlockTag.Final;
            }
            blocks.Add(
                new TimelineBlock
                {
                    BlockId = "long:b" + i,
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = i,
                    Kind = kind,
                    Tag = tag,
                    CallId = kind == TimelineBlockKind.Text ? null : "call-" + i,
                    CharCount = 8,
                    Preview = "preview",
                    Content = "content-" + i
                }
            );
        }
        long flushBefore = TimelineObservability.Snapshot().Samples;
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            blocks,
            MakeTurn(turn, TimelineTurnStatus.Completed, "long:b1000", blocks: 1000, tools: 62)
        );
        Assert.True(TimelineObservability.Snapshot().Samples > flushBefore);

        var summaryWatch = Stopwatch.StartNew();
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        summaryWatch.Stop();
        Assert.Single(summaries);
        Assert.True(
            summaryWatch.ElapsedMilliseconds < 1000,
            "summary took " + summaryWatch.ElapsedMilliseconds + "ms, budget 1000ms"
        );

        var probe = Enumerable.Range(1, 64).Select(i => "long:b" + (i * 7)).ToArray();
        var pointWatch = Stopwatch.StartNew();
        var (items, missing) = await _store.GetBlockContentsAsync(ws, sess, probe);
        pointWatch.Stop();
        Assert.Empty(missing);
        Assert.Equal(64, items.Count);
        Assert.True(
            pointWatch.ElapsedMilliseconds < 300,
            "point query took " + pointWatch.ElapsedMilliseconds + "ms, budget 300ms"
        );
    }

    [Fact]
    public void ObservabilityReportsFlushSlowAndCheckpoint()
    {
        long flushBefore = TimelineObservability.Snapshot().Samples;
        long slowBefore = TimelineObservability.Snapshot().SlowFlushCount;
        TimelineObservability.ReportFlush(5);
        TimelineObservability.ReportFlush(TimelineObservability.SlowFlushThresholdMs);
        var snap = TimelineObservability.Snapshot();
        Assert.Equal(flushBefore + 2, snap.Samples);
        Assert.Equal(slowBefore + 1, snap.SlowFlushCount);
        Assert.True(snap.MedianMs >= 0);

        long ckptBefore = snap.CheckpointFailCount;
        TimelineObservability.ReportCheckpoint(false);
        Assert.Equal(ckptBefore + 1, TimelineObservability.Snapshot().CheckpointFailCount);

        long walBefore = TimelineObservability.Snapshot().WalWarnCount;
        TimelineObservability.ReportWalSize(TimelineObservability.WalWarnThresholdBytes);
        Assert.Equal(walBefore + 1, TimelineObservability.Snapshot().WalWarnCount);
        Assert.Equal(
            TimelineObservability.WalWarnThresholdBytes,
            TimelineObservability.Snapshot().LastWalBytes
        );
    }
}
