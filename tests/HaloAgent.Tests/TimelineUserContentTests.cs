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
/// 历史区用户消息全文：turns.user_content 落盘、回读、老库回填与幂等语义。
/// 目录仍用 15 字预览，历史正文用全文。
/// </summary>
public sealed class TimelineUserContentTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryDb _db;
    private readonly SessionHistoryStore _store;

    public TimelineUserContentTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloUserContent_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloUserContentBoot_" + Guid.NewGuid().ToString("N")
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

    private static string LongUserText() =>
        "请帮我分析下面这段报错日志并给出修复步骤：" + new string('a', 60) + "😀😁😂🤣😃😄😅中文测试" + new string('b', 40);

    [Fact]
    public async Task AppendTurnRoundTripKeepsFullUserContent()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string full = LongUserText();
        Assert.True(full.Length > 100);

        string newMsgId = "u-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var userMsg = new UnifiedChatMessage
        {
            Id = newMsgId,
            Role = "user",
            Content = full
        };
        var turn = new TurnSummary
        {
            TurnId = "uturn-" + userMsg.Id,
            TurnSeq = 0,
            IterEnd = 1,
            UserPreview = TimelineBlockTracker.BuildPreview(full, 15),
            UserContent = full,
            Blocks = 0,
            Tools = 0,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage> { userMsg },
            new List<TimelineBlock>(),
            turn
        );

        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal(full, summaries[0].UserContent);
        Assert.Equal(TimelineBlockTracker.BuildPreview(full, 15), summaries[0].UserPreview);
        Assert.True(summaries[0].UserPreview.Length < full.Length);
    }

    [Fact]
    public void FinalizerEmitsPreviewPlusFullContent()
    {
        string full = LongUserText();
        var ctx = new TurnContext
        {
            WorkspaceId = "ws",
            SessionId = "sess",
            UserText = full,
            RequestId = "req-1",
        };
        ctx.UserMessage = new UnifiedChatMessage
        {
            Id = "u-finalizer-1",
            Role = "user",
            Content = full
        };

        var (_, turn) = TimelineFinalizer.Finalize(ctx, 1, TimelineTurnStatus.Completed);
        Assert.Equal(full, turn.UserContent);
        Assert.Equal(TimelineBlockTracker.BuildPreview(full, 15), turn.UserPreview);
    }

    [Fact]
    public async Task EmptyUpsertNeverWipesExistingContent()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string full = LongUserText();
        string newMsgId = "u-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var userMsg = new UnifiedChatMessage
        {
            Id = newMsgId,
            Role = "user",
            Content = full
        };
        string turnId = "uturn-" + userMsg.Id;

        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage> { userMsg },
            new List<TimelineBlock>(),
            new TurnSummary
            {
                TurnId = turnId,
                TurnSeq = 0,
                IterEnd = 1,
                UserPreview = TimelineBlockTracker.BuildPreview(full, 15),
                UserContent = full,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            }
        );

        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>(),
            new TurnSummary
            {
                TurnId = turnId,
                TurnSeq = 0,
                IterEnd = 2,
                UserPreview = TimelineBlockTracker.BuildPreview(full, 15),
                UserContent = string.Empty,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            }
        );

        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Single(summaries);
        Assert.Equal(full, summaries[0].UserContent);
    }

    [Fact]
    public async Task LegacyDatabaseBackfillsContentFromMessages()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string sess = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string dbPath = _db.EnsureDatabase(ws, sess);

        string userId = "u-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string turnId = "uturn-" + userId;
        string full = LongUserText();

        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "DROP TABLE turns;";
                drop.ExecuteNonQuery();
            }
            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE turns("
                    + "turn_id TEXT PRIMARY KEY,"
                    + "turn_seq INTEGER NOT NULL UNIQUE,"
                    + "iter_end INT NOT NULL,"
                    + "user_preview TEXT NOT NULL DEFAULT '',"
                    + "final_block_id TEXT,"
                    + "blocks INT NOT NULL DEFAULT 0,"
                    + "tools INT NOT NULL DEFAULT 0,"
                    + "failed INT NOT NULL DEFAULT 0,"
                    + "duration_ms INT NOT NULL DEFAULT 0,"
                    + "chars INT NOT NULL DEFAULT 0,"
                    + "status TEXT NOT NULL DEFAULT 'completed',"
                    + "last_request_id TEXT NOT NULL DEFAULT '',"
                    + "updated_at TEXT NOT NULL"
                    + ");";
                create.ExecuteNonQuery();
            }
            using (var msg = conn.CreateCommand())
            {
                msg.CommandText =
                    "INSERT OR IGNORE INTO messages(id, role, content, created_utc) VALUES($id, 'user', $content, $utc);";
                msg.Parameters.AddWithValue("$id", userId);
                msg.Parameters.AddWithValue("$content", full);
                msg.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                msg.ExecuteNonQuery();
            }
            using (var turn = conn.CreateCommand())
            {
                turn.CommandText =
                    "INSERT INTO turns(turn_id, turn_seq, iter_end, user_preview, updated_at) VALUES($tid, 1, 1, $preview, $utc);";
                turn.Parameters.AddWithValue("$tid", turnId);
                turn.Parameters.AddWithValue(
                    "$preview",
                    TimelineBlockTracker.BuildPreview(full, 15)
                );
                turn.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                turn.ExecuteNonQuery();
            }
            using (var miss = conn.CreateCommand())
            {
                miss.CommandText =
                    "INSERT INTO turns(turn_id, turn_seq, iter_end, user_preview, updated_at) VALUES('uturn-orphan-miss', 2, 1, 'o', $utc);";
                miss.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                miss.ExecuteNonQuery();
            }
        }

        _db.EnsureDatabase(ws, sess);

        var summaries = await _store.GetTimelineSummaryAsync(ws, sess);
        Assert.Equal(2, summaries.Count);
        var hit = summaries.Find(s => s.TurnId == turnId);
        Assert.NotNull(hit);
        Assert.Equal(full, hit!.UserContent);
        var missTurn = summaries.Find(s => s.TurnId == "uturn-orphan-miss");
        Assert.NotNull(missTurn);
        Assert.Equal(string.Empty, missTurn!.UserContent);
    }
}
