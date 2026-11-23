#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 工作区级单库验收：路径分区、会话隔离、注册表同步与对账、并发与存量保留。
/// </summary>
public sealed class WorkspaceDatabaseTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionHistoryDb _db;
    private readonly SessionHistoryStore _store;

    public WorkspaceDatabaseTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "HaloWsDb_" + Guid.NewGuid().ToString("N"));
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloWsDbBoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _db = new SessionHistoryDb(_storage);
        _store = new SessionHistoryStore(_storage);
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

    private static TurnSummary MakeTurn(string turnId, long seq = 0) =>
        new()
        {
            TurnId = turnId,
            TurnSeq = seq,
            IterEnd = 1,
            UserPreview = "u",
            UserContent = "user-" + turnId,
            Blocks = 1,
            Tools = 0,
            Status = TimelineTurnStatus.Completed,
            LastRequestId = "r",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };

    private static TimelineBlock MakeBlock(string blockId, string turnId, long seq = 1) =>
        new()
        {
            BlockId = blockId,
            TurnId = turnId,
            Iteration = 1,
            Sequence = seq,
            Kind = TimelineBlockKind.Text,
            Tag = TimelineBlockTag.Final,
            Preview = "p",
            Content = "content-" + blockId,
            CharCount = 8,
        };

    [Fact]
    public void WorkspaceDbPathIsWorkspaceLevelAndShared()
    {
        const string ws = "ws-shared";
        string dbPath = _storage.GetWorkspaceDbPath(ws);
        Assert.Equal(Path.Combine(_storage.SessionsDirectory, ws, "history.db"), dbPath);
        Assert.Equal(_db.GetWorkspaceDbPath(ws), dbPath);
        Assert.Equal(dbPath + "-wal", _storage.GetWorkspaceDbWalPath(ws));
        Assert.Equal(dbPath + "-shm", _storage.GetWorkspaceDbShmPath(ws));
        Assert.Equal(dbPath + ".bak", _storage.GetWorkspaceBackupPath(ws));
    }

    [Fact]
    public void EnsureWorkspaceDatabaseCreatesRegistryAndVersion()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string dbPath = _db.EnsureWorkspaceDatabase(ws);
        Assert.True(File.Exists(dbPath));
        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('sessions','messages','blocks','turns');";
            var names = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                names.Add(r.GetString(0));
            Assert.Contains("sessions", names);
            Assert.Contains("messages", names);
            Assert.Contains("blocks", names);
            Assert.Contains("turns", names);
        }
    }

    [Fact]
    public async Task TwoSessionsInOneWorkspaceAreIsolated()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        const string sessA = "sess_aaa";
        const string sessB = "sess_bbb";

        await _store.AppendTurnAsync(
            ws,
            sessA,
            new List<UnifiedChatMessage>(),
            new[] { MakeBlock("b:a1", "uturn-a1") },
            MakeTurn("uturn-a1")
        );
        await _store.AppendTurnAsync(
            ws,
            sessB,
            new List<UnifiedChatMessage>(),
            new[] { MakeBlock("b:b1", "uturn-b1") },
            MakeTurn("uturn-b1")
        );

        // 同一工作区共用一库文件
        Assert.True(File.Exists(_storage.GetWorkspaceDbPath(ws)));

        // 各自仅见己方回合
        var timeA = await _store.GetTimelineSummaryAsync(ws, sessA);
        var timeB = await _store.GetTimelineSummaryAsync(ws, sessB);
        Assert.Single(timeA);
        Assert.Single(timeB);
        Assert.Equal("uturn-a1", timeA[0].TurnId);
        Assert.Equal("uturn-b1", timeB[0].TurnId);
        Assert.Empty(await _store.GetTimelineSummaryAsync(ws, "sess_missing"));
    }

    [Fact]
    public async Task SameMessageIdAcrossSessionsCoexists()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var shared = UnifiedChatMessage.User("shared");
        _store.InsertSingleMessage(ws, "sess_one", shared);
        _store.InsertSingleMessage(ws, "sess_two", shared);

        var one = await _store.GetMessagesAsync(ws, "sess_one");
        var two = await _store.GetMessagesAsync(ws, "sess_two");
        Assert.Single(one);
        Assert.Single(two);
        Assert.Equal(shared.Id, one[0].Id);
        Assert.Equal(shared.Id, two[0].Id);
    }

    [Fact]
    public async Task RegisterAndReconcileRepairsRegistryFromMeta()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var repository = new SessionRepository(_storage);
        var service = new WorkspaceSessionService(repository);

        var meta = await service.CreateSessionAsync(ws, "hello world");
        Assert.Equal(3, meta.StorageFormatVersion);
        Assert.Equal("main", meta.SessionKind);

        // 注册表有行
        using (var conn = _db.OpenWorkspace(ws))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT storage_format, session_kind FROM sessions WHERE session_id = $sid;";
            cmd.Parameters.AddWithValue("$sid", meta.Id);
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(3, r.GetInt32(0));
            Assert.Equal("main", r.GetString(1));
        }

        // 人为改坏注册表后对账修复
        using (var conn = _db.OpenWorkspace(ws))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET storage_format = 999 WHERE session_id = $sid;";
            cmd.Parameters.AddWithValue("$sid", meta.Id);
            cmd.ExecuteNonQuery();
        }
        int fixedCount = await service.ReconcileWorkspaceRegistryAsync(ws);
        Assert.True(fixedCount >= 1);
        using (var conn = _db.OpenWorkspace(ws))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT storage_format FROM sessions WHERE session_id = $sid;";
            cmd.Parameters.AddWithValue("$sid", meta.Id);
            Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task LegacySessionWithoutMarkingIsSkippedByReconcile()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var repository = new SessionRepository(_storage);
        var service = new WorkspaceSessionService(repository);

        var legacy = new SessionMetadata
        {
            Id = "sess_legacy",
            WorkspaceId = ws,
            Title = "old"
        };
        Assert.Null(legacy.StorageFormatVersion);
        await repository.SaveSessionMetaAsync(ws, legacy);
        var index = await repository.LoadSessionsIndexAsync(ws);
        index.Insert(0, legacy);
        await repository.SaveSessionsIndexAsync(ws, index);

        int fixedCount = await service.ReconcileWorkspaceRegistryAsync(ws);
        Assert.Equal(0, fixedCount);
        _db.EnsureWorkspaceDatabase(ws);
        using var conn = _db.OpenWorkspace(ws);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_id = 'sess_legacy';";
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task ConcurrentAppendsAcrossSessionsDoNotLock()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var tasks = Enumerable
            .Range(0, 8)
            .Select(i =>
                _store.AppendTurnAsync(
                    ws,
                    "sess_c" + i,
                    new List<UnifiedChatMessage>(),
                    new[] { MakeBlock($"b:c{i}", $"uturn-c{i}") },
                    MakeTurn($"uturn-c{i}")
                )
            )
            .ToList();
        await Task.WhenAll(tasks);
        for (int i = 0; i < 8; i++)
        {
            var sum = await _store.GetTimelineSummaryAsync(ws, "sess_c" + i);
            Assert.Single(sum);
        }
    }

    [Fact]
    public async Task LegacyFilesArePreservedAndUnread()
    {
        string ws = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        const string sess = "sess_legacy_file";
        string sessionDir = _storage.GetSessionDirectory(ws, sess);
        Directory.CreateDirectory(sessionDir);
        string jsonl = Path.Combine(sessionDir, "messages.jsonl");
        string bak = Path.Combine(sessionDir, "messages.compact-bak-20240101.jsonl");
        string oldDb = Path.Combine(sessionDir, "history.db");
        await File.WriteAllTextAsync(jsonl, "{\"legacy\":true}\n");
        await File.WriteAllTextAsync(bak, "{\"bak\":true}\n");
        await File.WriteAllBytesAsync(oldDb, new byte[] { 1, 2, 3 });
        string jsonlBefore = await File.ReadAllTextAsync(jsonl);

        await _store.AppendTurnAsync(
            ws,
            "sess_other",
            new List<UnifiedChatMessage>(),
            new[] { MakeBlock("b:x", "uturn-x") },
            MakeTurn("uturn-x")
        );

        Assert.True(File.Exists(jsonl));
        Assert.True(File.Exists(bak));
        Assert.True(File.Exists(oldDb));
        Assert.Equal(jsonlBefore, await File.ReadAllTextAsync(jsonl));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(oldDb));
        // 工作区库与旧每会话库物理分离
        Assert.NotEqual(Path.GetFullPath(_storage.GetWorkspaceDbPath(ws)), Path.GetFullPath(oldDb));
    }
}
