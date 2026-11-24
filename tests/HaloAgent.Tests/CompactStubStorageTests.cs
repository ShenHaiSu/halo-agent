#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007-M1：压缩桩存储与桩写入（append-only）。</summary>
public sealed class CompactStubStorageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public CompactStubStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStub_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStubBoot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
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

    private async Task<(string wsId, string sessId)> NewSessionAsync()
    {
        string proj = Path.Combine(_tempRoot, "Proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(proj);
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "stub项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "stub会话");
        return (ws.Id, sess.Id);
    }

    private async Task BuildHistoryAsync(string wsId, string sessId, int pairs = 5)
    {
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.System("你是助手"));
        for (int i = 0; i < pairs; i++)
        {
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"问题{i}")
            );
            var a = UnifiedChatMessage.Assistant($"回答{i}");
            a.InputTokens = 1000 + i;
            a.OutputTokens = 50;
            await _service.AppendSessionMessageAsync(wsId, sessId, a);
        }
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.User("尾部追问"));
        var la = UnifiedChatMessage.Assistant("尾部回答");
        la.InputTokens = 2100;
        la.OutputTokens = 70;
        await _service.AppendSessionMessageAsync(wsId, sessId, la);
    }

    private static void SetStubMode(StoragePathManager storage)
    {
        var settings = storage.LoadAppSettings();
        settings.CompactMode = CompactMode.Stub;
        storage.SaveAppSettingsAsync(settings).GetAwaiter().GetResult();
    }

    [Fact(DisplayName = "P007-M1-S1: 迁移幂等，kind/meta_json 列存在")]
    public async Task Migration_Is_Idempotent()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 2);
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        string dbPath = _storageManager.GetWorkspaceDbPath(wsId);
        // v2 DB 可能尚未创建（纯消息会话）；先确保一次再断言幂等
        store.EnsureDatabaseForTest(wsId, sessId);
        var cols1 = ReadCols(dbPath, "messages");
        Assert.Contains("kind", cols1);
        Assert.Contains("meta_json", cols1);
        var turnCols1 = ReadCols(dbPath, "turns");
        Assert.Contains("kind", turnCols1);
        store.EnsureDatabaseForTest(wsId, sessId);
        var cols2 = ReadCols(dbPath, "messages");
        Assert.Equal(cols1.Count, cols2.Count);
        Assert.Equal(new HashSet<string>(cols1), new HashSet<string>(cols2));
    }

    [Fact(DisplayName = "P007-M1: legacy 路径行为与基线一致")]
    public async Task Legacy_Path_Unchanged()
    {
        // P007-M5: 默认已切 stub；legacy 路径用例显式钉回 legacy（断言零放宽）。
        var legacySettings = _storageManager.LoadAppSettings();
        legacySettings.CompactMode = CompactMode.Legacy;
        _storageManager.SaveAppSettingsAsync(legacySettings).GetAwaiter().GetResult();
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var before = await _service.GetSessionMessagesAsync(wsId, sessId);
        var compaction = new CompactionService(_service, _budgetService, new StubFakeGateway("要点"));
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        Assert.Null(result.StubMessageId);
        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.DoesNotContain(
            after,
            m => string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        Assert.True(after.Count < before.Count);
    }

    [Fact(DisplayName = "P007-M1: stub 压缩 append-only，桩行可读")]
    public async Task Stub_Compact_Is_Append_Only()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        var before = await _service.GetSessionMessagesAsync(wsId, sessId);
        var beforeIds = before.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        int beforeCount = before.Count;

        var compaction = new CompactionService(
            _service,
            _budgetService,
            new StubFakeGateway("摘要正文要点")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        Assert.NotNull(result.StubMessageId);
        Assert.True(result.RemovedMessages > 0);

        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        // 一条不删：旧集合 ⊂ 新集合，且 +1
        Assert.Equal(beforeCount + 1, after.Count);
        foreach (var id in beforeIds)
            Assert.Contains(after, m => string.Equals(m.Id, id, StringComparison.Ordinal));

        var stub = after.FirstOrDefault(m =>
            string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        Assert.NotNull(stub);
        Assert.Equal("user", stub!.Role);
        Assert.Contains("摘要正文要点", stub.Content ?? string.Empty);
        Assert.NotNull(stub.MetaJson);
        var meta = JsonSerializer.Deserialize<CompactStubMeta>(
            stub.MetaJson!,
            HaloJsonOptions.CompactCamelCase
        );
        Assert.NotNull(meta);
        Assert.Equal(1, meta!.V);
        Assert.Equal(result.RemovedMessages, meta.RemovedCount);
        Assert.Equal(result.SummaryOutputTokens, meta.SummaryTokens);

        // meta 冻结为 stub，只增 MessageCount
        var sessMeta = await _service.GetSessionAsync(wsId, sessId);
        Assert.NotNull(sessMeta);
        Assert.Equal(CompactMode.Stub, sessMeta!.CompactMode);
        Assert.Equal(beforeCount + 1, sessMeta.MessageCount);

        // 桩行经 DB 落盘：条数 +1 且末行即桩（user/compact_stub）
        var dbMsgs = await new SessionHistoryStore(
            new SessionHistoryDb(_storageManager)
        ).GetMessagesAsync(wsId, sessId);
        Assert.Equal(beforeCount + 1, dbMsgs.Count);
        var dbLast = dbMsgs[^1];
        Assert.Equal(stub!.Id, dbLast.Id);
        Assert.Equal(MessageKind.CompactStub, dbLast.Kind);
        Assert.Equal("user", dbLast.Role);
    }

    [Fact(DisplayName = "P007-M1: 桩 turn kind=compact，seq=MAX+1")]
    public async Task Stub_Turn_Is_Compact()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        // 预建 DB（含一条普通轮，保证 turns 非空）
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        store.EnsureDatabaseForTest(wsId, sessId);
        var seedTurn = new TurnSummary
        {
            TurnId = "uturn-seed",
            TurnSeq = 1,
            IterEnd = 1,
            UserPreview = "seed",
            UserContent = "seed",
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        await store.AppendTurnAsync(
            wsId,
            sessId,
            new List<UnifiedChatMessage> { UnifiedChatMessage.User("seed") },
            new List<TimelineBlock>(),
            seedTurn
        );
        var beforeTurns = await store.GetTimelineSummaryAsync(wsId, sessId);
        long maxBefore = beforeTurns.Count == 0 ? 0 : beforeTurns.Max(t => t.TurnSeq);

        var compaction = new CompactionService(_service, _budgetService, new StubFakeGateway("摘要"));
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);

        var afterTurns = await store.GetTimelineSummaryAsync(wsId, sessId);
        Assert.Equal(beforeTurns.Count + 1, afterTurns.Count);
        var stubTurn = afterTurns.FirstOrDefault(t =>
            string.Equals(t.Kind, TurnKind.Compact, StringComparison.Ordinal)
        );
        Assert.NotNull(stubTurn);
        Assert.StartsWith("cturn-", stubTurn!.TurnId, StringComparison.Ordinal);
        Assert.Equal(maxBefore + 1, stubTurn.TurnSeq);
        Assert.Equal("上下文压缩", stubTurn.UserPreview);
        Assert.Equal(result.RemovedMessages, stubTurn.RemovedMessages);
        Assert.Equal(0, stubTurn.Blocks);
        Assert.Null(stubTurn.FinalBlockId);
        // 旧轮一条不删
        foreach (var t in beforeTurns)
            Assert.Contains(
                afterTurns,
                x => string.Equals(x.TurnId, t.TurnId, StringComparison.Ordinal)
            );
    }

    [Fact(DisplayName = "P007-M1: 桩查询方法（LastSeq/LastStub/SinceSeq）")]
    public async Task Stub_Query_Methods()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 3);
        SetStubMode(_storageManager);
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        Assert.Null(await store.GetLastCompactStubAsync(wsId, sessId));
        Assert.Null(await store.GetLastCompactStubSeqAsync(wsId, sessId));
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new StubFakeGateway("查询摘要")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        var seq = await store.GetLastCompactStubSeqAsync(wsId, sessId);
        Assert.NotNull(seq);
        var info = await store.GetLastCompactStubAsync(wsId, sessId);
        Assert.NotNull(info);
        Assert.Equal(result.StubMessageId, info!.MessageId);
        Assert.Equal(result.RemovedMessages, info.RemovedCount);
        Assert.Contains("查询摘要", info.Summary);
        var since = await store.GetMessagesSinceSeqAsync(wsId, sessId, seq!.Value);
        Assert.True(since.Count >= 1);
        Assert.Equal(info.MessageId, since[0].Id);
        Assert.Equal(MessageKind.CompactStub, since[0].Kind);
    }

    [Fact(DisplayName = "P007-M1: 列映射完整（Kind/MetaJson/usage 回读一致）")]
    public async Task Column_Mapping_Roundtrip()
    {
        var (wsId, sessId) = await NewSessionAsync();
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        store.EnsureDatabaseForTest(wsId, sessId);
        var msg = UnifiedChatMessage.User("桩内容");
        msg.Kind = MessageKind.CompactStub;
        msg.MetaJson = "{\"v\":1}";
        msg.InputTokens = 11;
        msg.OutputTokens = 22;
        msg.CachedTokens = 33;
        store.InsertSingleMessageForTest(wsId, sessId, msg);
        var all = await store.GetMessagesAsync(wsId, sessId);
        var hit = all.FirstOrDefault(m => string.Equals(m.Id, msg.Id, StringComparison.Ordinal));
        Assert.NotNull(hit);
        Assert.Equal(MessageKind.CompactStub, hit!.Kind);
        Assert.Equal("{\"v\":1}", hit.MetaJson);
        Assert.Equal(11, hit.InputTokens);
        Assert.Equal(22, hit.OutputTokens);
        Assert.Equal(33, hit.CachedTokens);
    }

    private static HashSet<string> ReadCols(string dbPath, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(1))
                set.Add(r.GetString(1));
        }
        return set;
    }

    private sealed class StubFakeGateway : ILlmGatewayClient
    {
        private readonly string _content;

        public StubFakeGateway(string content) => _content = content;

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return UnifiedStreamEvent.Text(_content);
            yield return UnifiedStreamEvent.Tokens(100, 20, 0, 120);
            yield return UnifiedStreamEvent.Completed();
        }
    }
}

/// <summary>P007-M1 测试辅助：暴露 EnsureDatabase（生产代码幂等入口）。</summary>
internal static class SessionHistoryStoreTestHooks
{
    public static void EnsureDatabaseForTest(this SessionHistoryStore store, string ws, string sess)
    {
        var dbField = typeof(SessionHistoryStore).GetField(
            "_db",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        var db = (SessionHistoryDb)dbField!.GetValue(store)!;
        db.EnsureWorkspaceDatabase(ws);
    }

    public static void InsertSingleMessageForTest(
        this SessionHistoryStore store,
        string ws,
        string sess,
        UnifiedChatMessage msg
    )
    {
        store.InsertSingleMessage(ws, sess, msg);
    }
}
