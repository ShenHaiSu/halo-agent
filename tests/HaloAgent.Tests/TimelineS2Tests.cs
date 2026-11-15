#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S2 lazy loading: point queries, T2 tool append, large fileRef and branch copy.
/// </summary>
public sealed class TimelineS2Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryStore _store;

    public TimelineS2Tests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS2_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS2Boot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
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

    private static TurnSummary MakeTurn(string turnId) =>
        new()
        {
            TurnId = turnId,
            TurnSeq = 0,
            IterEnd = 1,
            UserPreview = "hi",
            FinalBlockId = turnId + ":b1",
            Blocks = 2,
            Tools = 1,
            Failed = 0,
            DurationMs = 10,
            Chars = 20,
            Status = TimelineTurnStatus.Completed,
            LastRequestId = "req1",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };

    private static TimelineBlock MakeText(string blockId, string turnId, string content) =>
        new()
        {
            BlockId = blockId,
            TurnId = turnId,
            Iteration = 1,
            Sequence = 1,
            Kind = TimelineBlockKind.Text,
            Tag = TimelineBlockTag.Final,
            CharCount = content.Length,
            Preview = content.Length > 50 ? content.Substring(0, 50) : content,
            Content = content,
        };

    [Fact]
    public async Task AppendToolResultUpdatesToolsAndIsPointQueryable()
    {
        const string ws = "ws-s2";
        const string sess = "sess-s2a";
        string turnId = "uturn-u1";
        var blocks = new List<TimelineBlock>
        {
            new()
            {
                BlockId = "req1:b1",
                TurnId = turnId,
                Iteration = 1,
                Sequence = 1,
                Kind = TimelineBlockKind.ToolCall,
                Tag = TimelineBlockTag.Process,
                CallId = "call-1",
                CharCount = 10,
                Preview = "{}",
                Content = "{\"a\":1}",
            },
            MakeText("req1:b2", turnId, "hello final"),
        };
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            blocks,
            MakeTurn(turnId)
        );
        var resultBlock = new TimelineBlock
        {
            BlockId = "req1:tool-call-1",
            TurnId = turnId,
            Iteration = 1,
            Sequence = 2,
            Kind = TimelineBlockKind.ToolResult,
            Tag = TimelineBlockTag.Process,
            CallId = "call-1",
            CharCount = 5,
            Preview = "ok",
            Content = "ok-result",
        };
        await _store.AppendToolResultAsync(ws, sess, turnId, resultBlock, isFailed: false);
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal(2, summaries[0].Tools);

        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "req1:b2", "req1:tool-call-1", "nope" }
        );
        Assert.Empty(missing.Where(m => m != "nope"));
        Assert.Contains("nope", missing);
        Assert.Equal("hello final", items["req1:b2"].Markdown);
        Assert.Equal(string.Empty, items["req1:tool-call-1"].Markdown);
        Assert.Equal("ok-result", items["req1:tool-call-1"].ResultText);

        var tool = await _store.GetToolResultAsync(ws, sess, turnId, "call-1");
        Assert.NotNull(tool);
        Assert.Equal("ok-result", tool!.ResultText);
    }

    [Fact]
    public async Task LargeBlockSpillsToFileRefWith4KPreview()
    {
        const string ws = "ws-s2";
        const string sess = "sess-s2big";
        string turnId = "uturn-big";
        string huge = new string('x', 70 * 1024);
        var block = MakeText("req-big:b1", turnId, huge);
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new[] { block },
            MakeTurn(turnId)
        );
        var (items, _) = await _store.GetBlockContentsAsync(ws, sess, new[] { "req-big:b1" });
        var got = items["req-big:b1"];
        Assert.True(got.Truncated);
        Assert.NotNull(got.Preview);
        Assert.True(got.Preview.Length <= 4096 + 16);
        string? contentRef = null;
        using (
            var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = _storageManager.GetHistoryDbPath(ws, sess),
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
                    Pooling = false,
                }.ToString()
            )
        )
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT content, content_ref FROM blocks WHERE block_id='req-big:b1';";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(string.Empty, r.GetString(0));
            contentRef = r.IsDBNull(1) ? null : r.GetString(1);
        }
        Assert.NotNull(contentRef);
        Assert.StartsWith("fileRef:tool-outputs/", contentRef, StringComparison.Ordinal);
        Assert.Equal(huge, got.Markdown);
    }

    [Fact]
    public void PreviewIsPlainTextGraphemeCut()
    {
        string emoji = "😀😀😀hello";
        string cut = TimelineBlockTracker.BuildPreview(emoji, 2);
        Assert.Equal("😀😀", cut);
    }

    [Fact]
    public void TagReasonsAreOnlyThreeValues()
    {
        Assert.Equal("demote", TimelineTagReason.Demote);
        Assert.Equal("finalize", TimelineTagReason.Finalize);
        Assert.Equal("flip", TimelineTagReason.Flip);
    }
}
