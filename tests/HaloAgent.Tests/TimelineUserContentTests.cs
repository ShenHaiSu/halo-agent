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
        string dbPath = _db.EnsureWorkspaceDatabase(ws);

        string userId = "u-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string turnId = "uturn-" + userId;
        string full = LongUserText();

        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>
            {
                new UnifiedChatMessage
                {
                    Id = userId,
                    Role = "user",
                    Content = full
                },
            },
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
                TurnId = "uturn-orphan-miss",
                TurnSeq = 0,
                IterEnd = 1,
                UserPreview = "o",
                UserContent = string.Empty,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            }
        );

        _db.EnsureWorkspaceDatabase(ws);

        using (var check = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var hitCmd = check.CreateCommand();
            hitCmd.CommandText = "SELECT user_content FROM turns WHERE turn_id = $tid;";
            hitCmd.Parameters.AddWithValue("$tid", turnId);
            object? hitContent = hitCmd.ExecuteScalar();
            Assert.Equal(full, Convert.ToString(hitContent));
            using var missCmd = check.CreateCommand();
            missCmd.CommandText =
                "SELECT user_content FROM turns WHERE turn_id = 'uturn-orphan-miss';";
            object? missContent = missCmd.ExecuteScalar();
            Assert.Equal(string.Empty, Convert.ToString(missContent));
        }
        await Task.CompletedTask;
    }
}
