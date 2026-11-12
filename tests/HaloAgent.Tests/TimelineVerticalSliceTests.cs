#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// First vertical slice: single round timeline persistence and summary.
/// Covers database bootstrap, single-transaction turn commit and tag finalization.
/// </summary>
public sealed class TimelineVerticalSliceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryDb _db;
    private readonly SessionHistoryStore _store;

    public TimelineVerticalSliceTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineSlice_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineSliceBoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _db = new SessionHistoryDb(_storageManager);
        _store = new SessionHistoryStore(_db);
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

    [Fact]
    public async Task EmptyDatabaseHasThreeTablesAndGuards()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string dbPath = _db.EnsureDatabase(ws, sess);
        Assert.True(File.Exists(dbPath));

        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using var tableCmd = conn.CreateCommand();
        tableCmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('messages','blocks','turns');";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = await tableCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }
        Assert.Contains("messages", names);
        Assert.Contains("blocks", names);
        Assert.Contains("turns", names);

        using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('ix_blocks_turn_seq','ix_blocks_call');";
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = await indexCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
        }
        Assert.Contains("ix_blocks_turn_seq", indexes);
        Assert.Contains("ix_blocks_call", indexes);

        async Task<string> QuerySingle(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            object? v = await c.ExecuteScalarAsync();
            return v?.ToString() ?? string.Empty;
        }
        Assert.Equal("wal", (await QuerySingle("PRAGMA journal_mode;")).ToLowerInvariant());
        Assert.Equal("2", await QuerySingle("PRAGMA synchronous;"));
        Assert.Equal("5000", await QuerySingle("PRAGMA busy_timeout;"));
        Assert.Equal("1", await QuerySingle("PRAGMA foreign_keys;"));

        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='blocks';";
        string? blocksSql = (string?)await checkCmd.ExecuteScalarAsync();
        Assert.NotNull(blocksSql);
        Assert.Contains("CHECK(tag IN ('process','final'))", blocksSql!);
    }

    [Fact]
    public async Task SingleTurnCommitsMessagesBlocksAndTurnInOneTransaction()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        var assistant = UnifiedChatMessage.Assistant("final answer", null, "thinking");
        var blocks = new List<TimelineBlock>
        {
            new()
            {
                BlockId = "req:b1",
                TurnId = "uturn-u1",
                Iteration = 1,
                Sequence = 1,
                Kind = TimelineBlockKind.Reasoning,
                Tag = TimelineBlockTag.Process,
                Preview = "think",
                Content = "thinking",
                CharCount = 8
            },
            new()
            {
                BlockId = "req:b2",
                TurnId = "uturn-u1",
                Iteration = 1,
                Sequence = 2,
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Process,
                Preview = "middle",
                Content = "middle text",
                CharCount = 11
            },
            new()
            {
                BlockId = "req:b3",
                TurnId = "uturn-u1",
                Iteration = 1,
                Sequence = 3,
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Final,
                Preview = "final answer",
                Content = "final answer",
                CharCount = 12
            },
        };
        var turn = new TurnSummary
        {
            TurnId = "uturn-u1",
            TurnSeq = 0,
            IterEnd = 1,
            UserPreview = "hello",
            FinalBlockId = "req:b3",
            FinalPreview = "final answer",
            Blocks = 3,
            Tools = 0,
            Failed = 0,
            DurationMs = 0,
            Chars = 31,
            Status = TimelineTurnStatus.Completed,
            LastRequestId = "req",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            AnchorKey = "uturn-u1",
        };

        long flushMs = await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage> { assistant },
            blocks,
            turn
        );
        Assert.True(flushMs >= 0);

        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal("uturn-u1", summaries[0].TurnId);
        Assert.Equal(1, summaries[0].TurnSeq);
        Assert.Equal("final answer", summaries[0].FinalPreview);
        Assert.Equal(TimelineTurnStatus.Completed, summaries[0].Status);

        using var conn = _db.Open(ws, sess);
        using var finalCmd = conn.CreateCommand();
        finalCmd.CommandText = "SELECT COUNT(*) FROM blocks WHERE tag='final';";
        long finalCount = (long)(await finalCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, finalCount);
    }

    [Fact]
    public async Task PendingTagNeverPersistsAndHealsToProcess()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var blocks = new List<TimelineBlock>
        {
            new()
            {
                BlockId = "req:bp1",
                TurnId = "uturn-u2",
                Iteration = 1,
                Sequence = 5,
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Pending,
                Preview = "draft",
                Content = "draft",
                CharCount = 5
            },
        };
        var turn = new TurnSummary
        {
            TurnId = "uturn-u2",
            IterEnd = 1,
            UserPreview = "hi",
            Blocks = 1,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            AnchorKey = "uturn-u2",
        };
        await _store.AppendTurnAsync(ws, sess, new List<UnifiedChatMessage>(), blocks, turn);

        using var conn = _db.Open(ws, sess);
        using var pendingCmd = conn.CreateCommand();
        pendingCmd.CommandText = "SELECT COUNT(*) FROM blocks WHERE tag='pending';";
        long pendingCount = (long)(await pendingCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, pendingCount);
        using var processCmd = conn.CreateCommand();
        processCmd.CommandText = "SELECT tag FROM blocks WHERE block_id='req:bp1';";
        string? tag = (string?)await processCmd.ExecuteScalarAsync();
        Assert.Equal("process", tag);
    }

    [Fact]
    public async Task ToolCountMatchesToolCallBlocks()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var blocks = new List<TimelineBlock>
        {
            new()
            {
                BlockId = "r:b1",
                TurnId = "uturn-u3",
                Iteration = 1,
                Sequence = 11,
                Kind = TimelineBlockKind.ToolCall,
                Tag = TimelineBlockTag.Process,
                CallId = "call-1",
                Preview = "args",
                Content = "{}",
                CharCount = 2
            },
            new()
            {
                BlockId = "r:b2",
                TurnId = "uturn-u3",
                Iteration = 1,
                Sequence = 12,
                Kind = TimelineBlockKind.ToolCall,
                Tag = TimelineBlockTag.Process,
                CallId = "call-2",
                Preview = "args",
                Content = "{}",
                CharCount = 2
            },
        };
        var turn = new TurnSummary
        {
            TurnId = "uturn-u3",
            IterEnd = 1,
            UserPreview = "run tools",
            Blocks = 2,
            Tools = 2,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            AnchorKey = "uturn-u3",
        };
        await _store.AppendTurnAsync(ws, sess, new List<UnifiedChatMessage>(), blocks, turn);
        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal(2, summaries[0].Tools);
    }

    [Fact]
    public void FinalizerPicksLastNonEmptyTextAsFinal()
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "ws",
            SessionId = "sess",
            UserText = "hello world, this is a longer user message",
            RequestId = "req-final",
        };
        ctx.TimelineLog.Add(
            new TimelineBlockDraft
            {
                BlockId = "req-final:b1",
                Sequence = 1,
                Epoch = "req-final",
                Kind = TimelineBlockKind.Reasoning,
                Tag = TimelineBlockTag.Process,
                Iteration = 1,
            }
        );
        ctx.TimelineLog[0].Text.Append("thinking");
        ctx.TimelineLog.Add(
            new TimelineBlockDraft
            {
                BlockId = "req-final:b2",
                Sequence = 2,
                Epoch = "req-final",
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Pending,
                Iteration = 1,
            }
        );
        ctx.TimelineLog[1].Text.Append("middle");
        ctx.TimelineLog.Add(
            new TimelineBlockDraft
            {
                BlockId = "req-final:b3",
                Sequence = 3,
                Epoch = "req-final",
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Pending,
                Iteration = 1,
            }
        );
        ctx.TimelineLog[2].Text.Append("final answer");

        var (blocks, turn) = TimelineFinalizer.Finalize(ctx, 1, false);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(TimelineBlockTag.Final, blocks[2].Tag);
        Assert.Equal(TimelineBlockTag.Process, blocks[0].Tag);
        Assert.Equal(TimelineBlockTag.Process, blocks[1].Tag);
        Assert.Equal(blocks[2].BlockId, turn.FinalBlockId);
        Assert.Equal(TimelineTurnStatus.Completed, turn.Status);
    }

    [Fact]
    public void FinalizerTruncationProducesNoFinal()
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "ws",
            SessionId = "sess",
            UserText = "hello",
            RequestId = "req-trunc",
        };
        ctx.TimelineLog.Add(
            new TimelineBlockDraft
            {
                BlockId = "req-trunc:b1",
                Sequence = 1,
                Epoch = "req-trunc",
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Pending,
                Iteration = 1,
            }
        );
        ctx.TimelineLog[0].Text.Append("partial");
        var (blocks, turn) = TimelineFinalizer.Finalize(ctx, 1, true);
        Assert.All(blocks, b => Assert.Equal(TimelineBlockTag.Process, b.Tag));
        Assert.Null(turn.FinalBlockId);
        Assert.Equal(TimelineTurnStatus.Truncated, turn.Status);
    }
}
