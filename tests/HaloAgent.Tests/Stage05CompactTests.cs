#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need05-03：/compact 上下文压缩（至少 5 个，需桩网关）。</summary>
public sealed class Stage05CompactTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public Stage05CompactTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage05Compact_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage05CompactBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        // P007-M5: 本文件覆盖 legacy 重写路径；默认已切 stub，此处显式钉回 legacy（断言零放宽）。
        _storageManager
            .SaveAppSettingsAsync(new AppSettingsConfig { CompactMode = CompactMode.Legacy })
            .GetAwaiter()
            .GetResult();
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
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "压缩项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "压缩会话");
        return (ws.Id, sess.Id);
    }

    /// <summary>15 条含 tool 链历史：system + 交替问答 + 末尾 tool 链。</summary>
    private async Task BuildHistoryAsync(string wsId, string sessId)
    {
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.System("你是助手"));
        for (int i = 0; i < 5; i++)
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
        var calls = new List<UnifiedToolCall>
        {
            new("call_c1", null, "read_file", "{\"p\":\"a\"}")
        };
        var ta = UnifiedChatMessage.Assistant("scanning", calls);
        ta.InputTokens = 2000;
        ta.OutputTokens = 60;
        await _service.AppendSessionMessageAsync(wsId, sessId, ta);
        await _service.AppendSessionMessageAsync(
            wsId,
            sessId,
            UnifiedChatMessage.Tool("call_c1", "file-content", "read_file")
        );
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.User("尾部追问"));
        var la = UnifiedChatMessage.Assistant("尾部回答");
        la.InputTokens = 2100;
        la.OutputTokens = 70;
        await _service.AppendSessionMessageAsync(wsId, sessId, la);
    }

    private async Task<List<UnifiedChatMessage>> DbMessagesAsync(string wsId, string sessId)
    {
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        return await store.GetMessagesAsync(wsId, sessId);
    }

    [Fact(DisplayName = "S05-03: 短历史 NoOp，不动盘、无 bak")]
    public async Task Compact_Short_History_NoOp()
    {
        var (wsId, sessId) = await NewSessionAsync();
        for (int i = 0; i < 5; i++)
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"q{i}")
            );

        var before = await DbMessagesAsync(wsId, sessId);
        string bakPath = _storageManager.GetWorkspaceBackupPath(wsId);
        bool bakBefore = File.Exists(bakPath);

        var compaction = new CompactionService(
            _service,
            _budgetService,
            new FakeGateway("要点1\n要点2")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        Assert.Equal(0, result.RemovedMessages);

        var after = await DbMessagesAsync(wsId, sessId);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(m => m.Id).ToList(), after.Select(m => m.Id).ToList());
        Assert.Equal(bakBefore, File.Exists(bakPath));
    }

    [Fact(DisplayName = "S05-03: 重写为 system+摘要+尾部，bak 存在，tool 无孤儿")]
    public async Task Compact_Rewrites_To_SystemPlusSummaryPlusTail()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var before = await DbMessagesAsync(wsId, sessId);
        Assert.True(before.Count >= 15);

        var compaction = new CompactionService(
            _service,
            _budgetService,
            new FakeGateway("要点1\n要点2")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        Assert.True(result.RemovedMessages > 0);

        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal("system", after[0].Role);
        Assert.Contains(
            after,
            m => m.Role == "user" && (m.Content ?? string.Empty).Contains("【上下文摘要")
        );
        // 摘要块含中文要点（桩回放）
        Assert.Contains(after, m => (m.Content ?? string.Empty).Contains("要点1"));
        // 尾部 tool 组无孤儿
        var calls = after.Where(m => m.ToolCalls != null && m.ToolCalls.Count > 0).ToList();
        var results = after.Where(m => m.Role == "tool").ToList();
        if (calls.Count == 0)
            Assert.Empty(results);
        else
        {
            var ids = calls.SelectMany(m => m.ToolCalls!).Select(c => c.CallId).ToHashSet();
            var rids = results.Select(r => r.ToolCallId!).ToHashSet();
            Assert.Subset(ids, rids);
        }
        // DB 对比：压缩后 = 压缩前 - 移除数 + 摘要 1 条
        Assert.Equal(before.Count - result.RemovedMessages + 1, after.Count);
        // 成功即删工作区库快照
        Assert.False(File.Exists(_storageManager.GetWorkspaceBackupPath(wsId)));
        if (!string.IsNullOrWhiteSpace(result.BackupFileName))
            Assert.False(File.Exists(result.BackupFileName));
        // meta.MessageCount 同步
        var meta = await _service.GetSessionAsync(wsId, sessId);
        Assert.Equal(after.Count, meta!.MessageCount);
    }

    [Fact(DisplayName = "S05-03: 网关失败时一切不动")]
    public async Task Compact_Failure_Keeps_Everything()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var beforeMsgs = await _service.GetSessionMessagesAsync(wsId, sessId);
        var metaBefore = await _service.GetSessionAsync(wsId, sessId);
        long billP = metaBefore!.CumulativePromptTokens;
        long billC = metaBefore.CumulativeCompletionTokens;

        var compaction = new CompactionService(_service, _budgetService, new ThrowingGateway());
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.False(result.Success);
        var afterMsgs = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal(beforeMsgs.Select(m => m.Id).ToList(), afterMsgs.Select(m => m.Id).ToList());
        var metaAfter = await _service.GetSessionAsync(wsId, sessId);
        Assert.Equal(billP, metaAfter!.CumulativePromptTokens);
        Assert.Equal(billC, metaAfter.CumulativeCompletionTokens);
    }

    [Fact(DisplayName = "S05-03: 压缩保留账单，占用快照不动")]
    public async Task Compact_Preserves_Billing()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var metaBefore = await _service.GetSessionAsync(wsId, sessId);
        long billP = metaBefore!.CumulativePromptTokens;
        long billC = metaBefore.CumulativeCompletionTokens;
        long totalIn = metaBefore.TotalInputTokens;

        var compaction = new CompactionService(_service, _budgetService, new FakeGateway("要点"));
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);

        var metaAfter = await _service.GetSessionAsync(wsId, sessId);
        Assert.Equal(billP, metaAfter!.CumulativePromptTokens);
        Assert.Equal(billC, metaAfter.CumulativeCompletionTokens);
        Assert.Equal(totalIn, metaAfter.TotalInputTokens); // 未知态由下轮校准，本轮不动
    }

    [Fact(DisplayName = "S05-03: keepLastGroups 生效，尾部恰为末 2 原子组")]
    public async Task Compact_Respects_Keep_Groups()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        var before = await _service.GetSessionMessagesAsync(wsId, sessId);

        var compaction = new CompactionService(_service, _budgetService, new FakeGateway("要点"));
        var result = await compaction.CompactAsync(wsId, sessId, keepLastGroups: 2);
        Assert.True(result.Success);

        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        // 尾部两条 == 压缩前末两条（简单交替尾部各自独立成组）
        Assert.Equal(before[^2].Content, after[^2].Content);
        Assert.Equal(before[^1].Content, after[^1].Content);
    }

    private sealed class FakeGateway : ILlmGatewayClient
    {
        private readonly string _content;

        public FakeGateway(string content) => _content = content;

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

    private sealed class ThrowingGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            throw new HttpRequestException("network down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
