#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Services.Context;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007-M5：压缩备份恢复（运维删桩与全量重写唯一入口）。</summary>
public sealed class CompactRestoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;

    public CompactRestoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentRestore_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentRestoreBoot_{Guid.NewGuid():N}"
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
        var settings = _storageManager.LoadAppSettings();
        settings.CompactMode = CompactMode.Stub;
        _storageManager.SaveAppSettingsAsync(settings).GetAwaiter().GetResult();
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
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "恢复项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "恢复会话");
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
    }

    private Task<CompactionResult> CompactOnceAsync(
        string wsId,
        string sessId,
        string summary = "摘要正文要点"
    ) =>
        new CompactionService(
            _service,
            new TokenBudgetService(),
            new ScriptedGateway(summary)
        ).CompactAsync(wsId, sessId);

    private async Task SimulatePhysicalLossAsync(
        string wsId,
        string sessId,
        IReadOnlyList<string> deleteIds
    )
    {
        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        var kept = full.Where(m => !deleteIds.Contains(m.Id, StringComparer.Ordinal)).ToList();
        await _repository.RewriteMessagesJsonlAsync(wsId, sessId, kept);
        string dbPath = _storageManager.GetWorkspaceDbPath(wsId);
        if (File.Exists(dbPath))
            await new SessionHistoryStore(
                new SessionHistoryDb(_storageManager)
            ).RewriteMessagesAsync(wsId, sessId, kept);
        var meta = await _repository.LoadSessionMetaAsync(wsId, sessId);
        if (meta != null)
        {
            meta.MessageCount = kept.Count;
            await _repository.SaveSessionMetaAsync(wsId, meta);
        }
    }

    [Fact(DisplayName = "P007-M5-R1: 基本恢复（行数/顺序/无重复）")]
    public async Task Basic_Restore()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var cr = await CompactOnceAsync(wsId, sessId);
        Assert.True(cr.Success);
        Assert.False(string.IsNullOrWhiteSpace(cr.BackupFileName));
        var compacted = await _service.GetSessionMessagesAsync(wsId, sessId);
        var bakIds = compacted.Take(compacted.Count - 1).Select(m => m.Id).ToList();
        var deleteIds = compacted.Where(m => m.Role != "system").Take(3).Select(m => m.Id).ToList();
        await SimulatePhysicalLossAsync(wsId, sessId, deleteIds);
        var tool = new CompactionRestore(_service);
        var r = await tool.RestoreAsync(wsId, sessId, cr.BackupFileName!);
        Assert.True(r.Success);
        Assert.False(r.NoOp);
        Assert.Equal(3, r.Restored);
        var merged = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal(bakIds.Count, r.MergedTotal);
        Assert.Equal(bakIds.Count, merged.Count);
        var expected = bakIds
            .Where(id => !deleteIds.Contains(id, StringComparer.Ordinal))
            .Concat(deleteIds.Where(id => bakIds.Contains(id, StringComparer.Ordinal)))
            .ToList();
        Assert.Equal(expected, merged.Select(m => m.Id).ToList());
        Assert.Equal(
            merged.Count,
            merged.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count()
        );
    }

    [Fact(DisplayName = "P007-M5-R2: 恢复幂等（二次 NoOp）")]
    public async Task Restore_Is_Idempotent()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var cr = await CompactOnceAsync(wsId, sessId);
        Assert.True(cr.Success);
        var compacted = await _service.GetSessionMessagesAsync(wsId, sessId);
        var deleteIds = compacted.Where(m => m.Role != "system").Take(2).Select(m => m.Id).ToList();
        await SimulatePhysicalLossAsync(wsId, sessId, deleteIds);
        var tool = new CompactionRestore(_service);
        var first = await tool.RestoreAsync(wsId, sessId, cr.BackupFileName!);
        Assert.True(first.Success);
        int count = (await _service.GetSessionMessagesAsync(wsId, sessId)).Count;
        var second = await tool.RestoreAsync(wsId, sessId, cr.BackupFileName!);
        Assert.True(second.Success);
        Assert.True(second.NoOp);
        Assert.Equal(count, (await _service.GetSessionMessagesAsync(wsId, sessId)).Count);
    }

    [Fact(DisplayName = "P007-M5-R3: 恢复后桩清理（无桩行/桩轮）")]
    public async Task Restore_Cleans_Stub()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var cr = await CompactOnceAsync(wsId, sessId);
        Assert.True(cr.Success);
        var compacted = await _service.GetSessionMessagesAsync(wsId, sessId);
        var deleteIds = compacted.Where(m => m.Role != "system").Take(1).Select(m => m.Id).ToList();
        await SimulatePhysicalLossAsync(wsId, sessId, deleteIds);
        var r = await new CompactionRestore(_service).RestoreAsync(
            wsId,
            sessId,
            cr.BackupFileName!
        );
        Assert.True(r.Success);
        var merged = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.DoesNotContain(
            merged,
            m => string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        var turns = await store.GetTimelineSummaryAsync(wsId, sessId);
        Assert.DoesNotContain(
            turns,
            t => (t.TurnId ?? string.Empty).StartsWith("cturn-", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            turns,
            t => string.Equals(t.Kind, TurnKind.Compact, StringComparison.Ordinal)
        );
    }

    [Fact(DisplayName = "P007-M5-R4a: 缺 bak 零变化")]
    public async Task Restore_Missing_Backup_No_Change()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        await CompactOnceAsync(wsId, sessId);
        int before = (await _service.GetSessionMessagesAsync(wsId, sessId)).Count;
        var r = await new CompactionRestore(_service).RestoreAsync(
            wsId,
            sessId,
            "messages.compact-bak-20990101000000.jsonl"
        );
        Assert.False(r.Success);
        Assert.Equal("BACKUP_NOT_FOUND", r.ErrorCode);
        Assert.Equal(before, (await _service.GetSessionMessagesAsync(wsId, sessId)).Count);
    }

    [Fact(DisplayName = "P007-M5-R4b: 桩无备份引用零变化")]
    public async Task Restore_Without_BackupRef_No_Change()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        string bak = await _repository.BackupMessagesJsonlAsync(wsId, sessId);
        var stub = UnifiedChatMessage.User("手工桩摘要");
        stub.Kind = MessageKind.CompactStub;
        stub.MetaJson = JsonSerializer.Serialize(
            new CompactStubMeta { RemovedCount = 3, BackupFile = null },
            HaloJsonOptions.CompactCamelCase
        );
        await _service.AppendSessionMessageAsync(wsId, sessId, stub);
        await _service.AppendCompactTurnAsync(
            wsId,
            sessId,
            stub,
            new TurnSummary
            {
                TurnId = "cturn-" + stub.Id,
                Kind = TurnKind.Compact,
                IterEnd = 1,
                UserPreview = "上下文压缩",
                UserContent = "手工桩摘要",
                RemovedMessages = 3,
                Blocks = 0,
                Tools = 0,
                Failed = 0,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            }
        );
        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        var victim = full.First(m => m.Role == "user" && m.Id != stub.Id).Id;
        await SimulatePhysicalLossAsync(wsId, sessId, new[] { victim });
        int before = (await _service.GetSessionMessagesAsync(wsId, sessId)).Count;
        var r = await new CompactionRestore(_service).RestoreAsync(wsId, sessId, bak);
        Assert.False(r.Success);
        Assert.Equal("NO_BACKUP", r.ErrorCode);
        Assert.Equal(before, (await _service.GetSessionMessagesAsync(wsId, sessId)).Count);
    }

    [Fact(DisplayName = "P007-M5-R5: 恢复后可发包（活跃=全量）")]
    public async Task Restored_History_Is_Sendable()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var cr = await CompactOnceAsync(wsId, sessId);
        Assert.True(cr.Success);
        var compacted = await _service.GetSessionMessagesAsync(wsId, sessId);
        await SimulatePhysicalLossAsync(
            wsId,
            sessId,
            compacted.Where(m => m.Role != "system").Take(2).Select(m => m.Id).ToList()
        );
        var r = await new CompactionRestore(_service).RestoreAsync(
            wsId,
            sessId,
            cr.BackupFileName!
        );
        Assert.True(r.Success);
        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        var active = await _service.GetActiveContextMessagesAsync(wsId, sessId);
        Assert.Equal(-1, ActiveContext.FindLastStubIndex(active));
        Assert.Equal(full.Select(m => m.Id).ToList(), active.Select(m => m.Id).ToList());
    }

    [Fact(DisplayName = "P007-M5: 会话级冻结（全局切 legacy 有桩仍 stub）")]
    public async Task Frozen_Session_Stays_Stub()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        Assert.True((await CompactOnceAsync(wsId, sessId)).Success);
        var settings = _storageManager.LoadAppSettings();
        settings.CompactMode = CompactMode.Legacy;
        await _storageManager.SaveAppSettingsAsync(settings);
        for (int i = 0; i < 4; i++)
        {
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"续问{i}")
            );
            var a = UnifiedChatMessage.Assistant($"续答{i}");
            a.InputTokens = 900;
            await _service.AppendSessionMessageAsync(wsId, sessId, a);
        }
        var before = await _service.GetSessionMessagesAsync(wsId, sessId);
        var second = await CompactOnceAsync(wsId, sessId, "二次摘要");
        Assert.True(second.Success);
        Assert.False(string.IsNullOrWhiteSpace(second.StubMessageId));
        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal(before.Count + 1, after.Count);
        foreach (string id in before.Select(m => m.Id))
            Assert.Contains(after, m => string.Equals(m.Id, id, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "P007-M5: 老式字符串桩回填幂等")]
    public async Task Legacy_String_Stub_Backfills()
    {
        var (wsId, sessId) = await NewSessionAsync();
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        store.EnsureDatabaseForTest(wsId, sessId);
        var old = UnifiedChatMessage.User("【上下文摘要：压缩自此前 5 条】旧摘要正文");
        store.InsertSingleMessageForTest(wsId, sessId, old);
        var db = new SessionHistoryDb(_storageManager);
        db.EnsureWorkspaceDatabase(wsId);
        var hit = (await store.GetMessagesAsync(wsId, sessId)).First(m => m.Id == old.Id);
        Assert.Equal(MessageKind.CompactStub, hit.Kind);
        Assert.Null(hit.MetaJson);
        db.EnsureWorkspaceDatabase(wsId);
        Assert.Single((await store.GetMessagesAsync(wsId, sessId)).Where(m => m.Id == old.Id));
    }

    [Fact(DisplayName = "P007-M5: 摘要 prompt 含最近一轮要点")]
    public void Compressor_Prompt_Covers_Latest_Turn()
    {
        Assert.Contains("最近一轮的具体结论与下一步动作", CompactionService.CompressorSystemPrompt);
    }

    private sealed class ScriptedGateway : ILlmGatewayClient
    {
        private readonly Queue<string> _summaries;

        public ScriptedGateway(params string[] summaries) =>
            _summaries = new Queue<string>(summaries);

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return UnifiedStreamEvent.Text(
                _summaries.Count > 0 ? _summaries.Dequeue() : "默认摘要"
            );
            yield return UnifiedStreamEvent.Tokens(100, 20, 0, 120);
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
