#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 历史孤儿只读投影的存储查询与 Bridge 端到端回归：投影可见、绝不写库、活跃轮与 compact 边界正确。
/// </summary>
public sealed class HistoryOrphanProjectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly SessionHistoryStore _store;
    private readonly WorkspaceInfo _workspace;

    public HistoryOrphanProjectionTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloOrphanProjection_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloOrphanProjectionBoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _store = new SessionHistoryStore(_storageManager);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(Path.Combine(_tempRoot, "Project"), "历史投影测试")
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        TryDelete(_tempRoot);
        TryDelete(_bootstrapRoot);
    }

    [Fact]
    public async Task GetUserTurnsAsync_ReadsOnlyRequestedIdsWithContentAndSeq()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-query");
        await WriteMessagesAsync(
            session.Id,
            User("user-first", "第一条", 1),
            User("user-second", "第二条", 2)
        );

        var users = await _store.GetUserTurnsAsync(
            _workspace.Id,
            session.Id,
            new[] { "user-first", "user-second" }
        );

        Assert.Equal(2, users.Count);
        var first = Assert.Single(users, u => u.Id == "user-first");
        Assert.Equal("第一条", first.Content);
        Assert.True(first.MessageSeq > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.CreatedAtUtc));
    }

    [Fact]
    public async Task GetUserTurnsAsync_EmptyIdsAndMissingDatabaseReturnEmpty()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-empty");

        Assert.Empty(
            await _store.GetUserTurnsAsync(_workspace.Id, session.Id, Array.Empty<string>())
        );
        Assert.Empty(
            await _store.GetUserTurnsAsync(
                _workspace.Id,
                "sess-not-created",
                new[] { "user-ghost" }
            )
        );
    }

    [Fact]
    public async Task GetUserTurnsAsync_DeduplicatesIds()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-dedup");
        await WriteMessagesAsync(session.Id, User("user-only", "唯一", 1));

        var users = await _store.GetUserTurnsAsync(
            _workspace.Id,
            session.Id,
            new[] { "user-only", "user-only", "user-only" }
        );

        Assert.Single(users);
    }

    [Fact]
    public async Task GetTimelineSummary_ProjectsHistoricalOrphanWithoutWritingDatabase()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-e2e");
        await SeedNormalTurnAndOrphanAsync(session.Id);
        var turnsBefore = await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id);
        Assert.Single(turnsBefore); // 物理库只有 user-current 一轮。

        var facade = new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);
        var response = await facade.GetTimelineSummary(_workspace.Id, session.Id);

        Assert.True(response.Ok);
        var turns = response.Data!;
        Assert.Equal(2, turns.Count);
        var orphan = Assert.Single(turns, t => t.TurnId == "uturn-user-old");
        Assert.Equal(TimelineTurnStatus.Cancelled, orphan.Status);
        Assert.Equal(string.Empty, orphan.LastRequestId);
        Assert.Equal("user-old", orphan.UserMessageId);
        Assert.Null(orphan.AssistantMessageId);
        Assert.Equal(0, orphan.Blocks);
        Assert.Equal("uturn-user-old", turns.Single(t => t.UserMessageId == "user-old").AnchorKey);
        Assert.Equal("uturn-user-old", turns[0].TurnId); // 孤儿按消息顺序排在前面。
        Assert.Equal("uturn-user-current", turns[1].TurnId);
        Assert.Equal(new long[] { 1, 2 }, turns.Select(t => t.TurnSeq).ToArray());

        var turnsAfter = await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id);
        Assert.Single(turnsAfter); // 投影绝不写库。
        Assert.Equal(turnsBefore[0].TurnSeq, turnsAfter[0].TurnSeq);

        // messages 快照也一致：投影只读正文，不新增/修改任何消息行。
        var refsBefore = await _store.GetMessageRefsAsync(_workspace.Id, session.Id);
        var usersBefore = await _store.GetUserTurnsAsync(
            _workspace.Id,
            session.Id,
            refsBefore.Select(r => r.Id).ToArray()
        );
        var refsAfter = await _store.GetMessageRefsAsync(_workspace.Id, session.Id);
        var usersAfter = await _store.GetUserTurnsAsync(
            _workspace.Id,
            session.Id,
            refsAfter.Select(r => r.Id).ToArray()
        );
        Assert.Equal(refsBefore, refsAfter);
        Assert.Equal(usersBefore, usersAfter);
    }

    [Fact]
    public async Task GetTimelineSummary_SkipsLatestUserWhenSessionActive()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-active");
        await WriteMessagesAsync(session.Id, User("user-old", "旧", 1), User("user-latest", "新", 2));
        var facade = new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);
        InjectActiveToken(facade, session.Id);

        var response = await facade.GetTimelineSummary(_workspace.Id, session.Id);

        Assert.True(response.Ok);
        var turns = response.Data!;
        Assert.Equal("uturn-user-old", Assert.Single(turns).TurnId);
        Assert.DoesNotContain(turns, t => t.TurnId == "uturn-user-latest");
    }

    [Fact]
    public async Task GetTimelineSummary_DoesNotProjectCompactStubOrLegacyPrefix()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-compact");
        await WriteMessagesAsync(
            session.Id,
            new UnifiedChatMessage
            {
                Id = "compact-stub",
                Role = "user",
                Content = "压缩桩",
                Kind = "compact_stub",
                CreatedAtUtc = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc),
            },
            User("legacy-summary", "【上下文摘要】历史压缩", 2),
            User("user-real", "真实孤儿", 3)
        );

        var facade = new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);
        var response = await facade.GetTimelineSummary(_workspace.Id, session.Id);

        Assert.True(response.Ok);
        var turns = response.Data!;
        Assert.Equal("uturn-user-real", Assert.Single(turns).TurnId);
        Assert.DoesNotContain(turns, t => t.TurnId == "uturn-compact-stub");
        Assert.DoesNotContain(turns, t => t.TurnId == "uturn-legacy-summary");
    }

    [Fact]
    public async Task GetTimelineSummary_EmptySessionReturnsEmpty()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-orphan-blank");
        var facade = new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);

        var response = await facade.GetTimelineSummary(_workspace.Id, session.Id);

        Assert.True(response.Ok);
        Assert.Empty(response.Data!);
    }

    private async Task SeedNormalTurnAndOrphanAsync(string sessionId)
    {
        var user = User("user-current", "当前", 1);
        var assistant = UnifiedChatMessage.Assistant("已回答");
        await _store.AppendTurnAsync(
            _workspace.Id,
            sessionId,
            new[] { user, assistant },
            new List<TimelineBlock>(),
            new TurnSummary
            {
                TurnId = "uturn-user-current",
                UserPreview = "当前",
                UserContent = "当前",
                Status = TimelineTurnStatus.Completed,
            }
        );
        // 注入一个没有 turns 摘要的更早 user，模拟历史孤儿（只重写 messages 分区）。
        await WriteMessagesAsync(sessionId, User("user-old", "旧孤儿", 1), user, assistant);
    }

    private async Task WriteMessagesAsync(string sessionId, params UnifiedChatMessage[] messages)
    {
        await _store.RewriteMessagesAsync(_workspace.Id, sessionId, messages);
    }

    private static UnifiedChatMessage User(string id, string content, int minute) =>
        new()
        {
            Id = id,
            Role = "user",
            Content = content,
            CreatedAtUtc = new DateTime(2026, 11, 26, 0, minute, 0, DateTimeKind.Utc),
        };

    private static void InjectActiveToken(HostBridgeFacade facade, string sessionId)
    {
        var field = typeof(HostBridgeFacade).GetField(
            "_activeSessionTokens",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(field);
        var tokens =
            (ConcurrentDictionary<string, CancellationTokenSource>)field!.GetValue(facade)!;
        tokens["agent:" + sessionId.Trim()] = new CancellationTokenSource();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }
}
