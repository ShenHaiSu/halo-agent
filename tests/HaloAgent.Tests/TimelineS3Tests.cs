#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S3 全链路收敛：DB messages 统一读取 + turn_seq 切面 + v1 合成 + 分支/GC/完整性.
/// </summary>
public sealed class TimelineS3Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryStore _store;

    public TimelineS3Tests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS3_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS3Boot_" + Guid.NewGuid().ToString("N")
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

    private static TurnSummary MakeTurn(string turnId, long seq) =>
        new()
        {
            TurnId = turnId,
            TurnSeq = seq,
            IterEnd = 1,
            UserPreview = "u",
            Blocks = 1,
            Tools = 0,
            Status = TimelineTurnStatus.Completed,
            LastRequestId = "r",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        };

    [Fact]
    public async Task DbMessagesRoundtripKeepsToolCallIdAndImages()
    {
        const string ws = "ws-s3";
        const string sess = "sess-s3msg";
        var user = UnifiedChatMessage.User("hi");
        var tool = UnifiedChatMessage.Tool("call-1", "out", "view_image");
        tool.Images = new List<ChatImageRef>
        {
            new()
            {
                ImageId = "img_1",
                FileName = "a.png",
                Mime = "image/png",
                RelPath = "attachments/a.png",
                Width = 1,
                Height = 1,
                SizeBytes = 10,
                Sha16 = "aaa"
            }
        };
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage> { user, tool },
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "r1:b1",
                    TurnId = "uturn-" + user.Id,
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.Text,
                    Tag = TimelineBlockTag.Final,
                    CharCount = 2,
                    Preview = "hi",
                    Content = "hi"
                },
            },
            new TurnSummary
            {
                TurnId = "uturn-" + user.Id,
                TurnSeq = 0,
                IterEnd = 1,
                UserPreview = "hi",
                FinalBlockId = "r1:b1",
                FinalPreview = "hi",
                Blocks = 1,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            }
        );
        // dual-write path: single tool message mirror
        _store.InsertSingleMessage(ws, sess, tool);
        var back = await _store.GetMessagesAsync(ws, sess);
        var hit = back.FirstOrDefault(m => m.ToolCallId == "call-1");
        Assert.NotNull(hit);
        Assert.NotNull(hit!.Images);
        Assert.Single(hit.Images!);
    }

    [Fact]
    public async Task TruncateFromTurnSeqDeletesSuffixTurnsAndBlocks()
    {
        const string ws = "ws-s3";
        const string sess = "sess-s3trunc";
        for (int i = 1; i <= 3; i++)
        {
            string tid = $"uturn-u{i}";
            await _store.AppendTurnAsync(
                ws,
                sess,
                new List<UnifiedChatMessage>
                {
                    new UnifiedChatMessage
                    {
                        Id = $"u{i}",
                        Role = "user",
                        Content = $"q{i}"
                    }
                },
                new List<TimelineBlock>
                {
                    new()
                    {
                        BlockId = $"b{i}",
                        TurnId = tid,
                        Iteration = 1,
                        Sequence = i,
                        Kind = TimelineBlockKind.Text,
                        Tag = TimelineBlockTag.Final,
                        CharCount = 1,
                        Preview = "x",
                        Content = "x"
                    }
                },
                MakeTurn(tid, 0)
            );
        }
        var before = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Equal(3, before.Count);
        long cut = before.OrderBy(s => s.TurnSeq).ElementAt(1).TurnSeq;
        await _store.TruncateFromTurnSeqAsync(ws, sess, cut);
        var after = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(after);
        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "b1", "b2", "b3" }
        );
        Assert.True(items.ContainsKey("b1"));
        Assert.Contains("b2", missing);
        Assert.Contains("b3", missing);
    }

    [Fact]
    public async Task SynthesizeV2FromMessagesFollowsBranchRules()
    {
        const string ws = "ws-s3";
        const string sess = "sess-s3synth";
        var assistant = UnifiedChatMessage.Assistant("final answer");
        var toolNoCall = UnifiedChatMessage.Tool(string.Empty, "orphan output", "some_tool");
        toolNoCall.ToolCallId = null;
        var prefix = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("q"),
            assistant,
            toolNoCall,
        };
        await _store.SynthesizeV2FromMessagesAsync(ws, sess, prefix);
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal(TimelineTurnStatus.Completed, summaries[0].Status);
        var ids = await _store.GetBlockIdsByTurnAsync(ws, sess, summaries[0].TurnId);
        Assert.Equal(2, ids.Count);
        var (items, missing) = await _store.GetBlockContentsAsync(ws, sess, ids);
        Assert.Empty(missing);
        // tool_result with NULL call_id gets placeholder preview
        var toolBlock = items.Values.FirstOrDefault(v => v.Kind == TimelineBlockKind.ToolResult);
        Assert.NotNull(toolBlock);
        Assert.Equal("[历史工具结果]", toolBlock!.Preview);
        // assistant text becomes final
        var textBlock = items.Values.FirstOrDefault(v => v.Kind == TimelineBlockKind.Text);
        Assert.NotNull(textBlock);
        Assert.Equal(TimelineBlockTag.Final, textBlock!.Tag);
    }

    [Fact]
    public async Task BranchCopyKeepsOnlyReferencedOutputsAndGcRemovesOrphans()
    {
        const string ws = "ws-s3";
        const string src = "sess-s3src";
        const string dst = "sess-s3dst";
        string huge = new string('y', 70 * 1024);
        await _store.AppendTurnAsync(
            ws,
            src,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "big:b1",
                    TurnId = "uturn-u1",
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.Text,
                    Tag = TimelineBlockTag.Final,
                    CharCount = huge.Length,
                    Preview = "y",
                    Content = huge
                }
            },
            MakeTurn("uturn-u1", 0)
        );
        Assert.True(_store.CopyHistoryForBranchAsync(ws, src, ws, dst).Result);
        var dstSummaries = await _store.GetTimelineSummaryAsync(ws, dst);
        Assert.Single(dstSummaries);
        // orphan file: create extra txt then GC should remove it
        string outDir = _storageManager.GetSessionToolOutputsDir(ws, dst);
        Directory.CreateDirectory(outDir);
        string orphan = Path.Combine(outDir, "orphan.txt");
        await File.WriteAllTextAsync(orphan, "junk");
        int removed = await _store.CollectOrphanToolOutputsAsync(ws, dst);
        Assert.Equal(1, removed);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task IntegrityCheckPassesOnHealthyDb()
    {
        const string ws = "ws-s3";
        const string sess = "sess-s3integ";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "i:b1",
                    TurnId = "uturn-u1",
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.Text,
                    Tag = TimelineBlockTag.Final,
                    CharCount = 1,
                    Preview = "x",
                    Content = "x"
                }
            },
            MakeTurn("uturn-u1", 0)
        );
        var (ok, _) = await _store.CheckIntegrityAsync(ws, sess);
        Assert.True(ok);
    }
}
