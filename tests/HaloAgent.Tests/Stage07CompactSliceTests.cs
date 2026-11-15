#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Services.Context;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007-M2：活跃切片读侧切换集成验收（I1–I5 发包切片与多轮压缩）。</summary>
public sealed class Stage07CompactSliceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public Stage07CompactSliceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage07Slice_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage07SliceBoot_{Guid.NewGuid():N}"
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
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService(null, _service);
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
        var ws = await _service.AddOrUpdateWorkspaceAsync(proj, "切片项目");
        var sess = await _service.CreateSessionAsync(ws.Id, "切片会话");
        return (ws.Id, sess.Id);
    }

    private async Task BuildHistoryAsync(
        string wsId,
        string sessId,
        int pairs = 5,
        string marker = "q"
    )
    {
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.System("你是助手"));
        for (int i = 0; i < pairs; i++)
        {
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"{marker}{i}")
            );
            var a = UnifiedChatMessage.Assistant($"{marker}-a{i}");
            a.InputTokens = 1000 + i;
            a.OutputTokens = 50;
            await _service.AppendSessionMessageAsync(wsId, sessId, a);
        }
    }

    private static void SetStubMode(StoragePathManager storage)
    {
        var settings = storage.LoadAppSettings();
        settings.CompactMode = CompactMode.Stub;
        storage.SaveAppSettingsAsync(settings).GetAwaiter().GetResult();
    }

    private static List<string> Ids(IReadOnlyList<UnifiedChatMessage> list) =>
        list.Select(m => m.Id).ToList();

    [Fact(DisplayName = "P007-M2-I1: 压缩后 append-only，旧行全留且仅 +1")]
    public async Task Compact_Is_Append_Only()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        var before = await _service.GetSessionMessagesAsync(wsId, sessId);
        var beforeIds = before.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("摘要正文要点")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        Assert.NotNull(result.StubMessageId);
        var after = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal(before.Count + 1, after.Count);
        foreach (string id in beforeIds)
            Assert.Contains(after, m => string.Equals(m.Id, id, StringComparison.Ordinal));
        var stub = after.FirstOrDefault(m =>
            string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        Assert.NotNull(stub);
        Assert.Equal("user", stub!.Role);
    }

    [Fact(DisplayName = "P007-M2-I2: 压缩后 turns +1，桩轮 kind=compact 且旧轮全留")]
    public async Task Compact_Turns_Append_Only()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
        var beforeTurns = await store.GetTimelineSummaryAsync(wsId, sessId);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("摘要")
        );
        var result = await compaction.CompactAsync(wsId, sessId);
        Assert.True(result.Success);
        var afterTurns = await store.GetTimelineSummaryAsync(wsId, sessId);
        Assert.Equal(beforeTurns.Count + 1, afterTurns.Count);
        var stubTurn = afterTurns.FirstOrDefault(t =>
            string.Equals(t.Kind, TurnKind.Compact, StringComparison.Ordinal)
        );
        Assert.NotNull(stubTurn);
        Assert.StartsWith("cturn-", stubTurn!.TurnId, StringComparison.Ordinal);
        Assert.Equal(result.RemovedMessages, stubTurn.RemovedMessages);
        Assert.Equal(0, stubTurn.Blocks);
        Assert.Null(stubTurn.FinalBlockId);
        foreach (var t in beforeTurns)
            Assert.Contains(
                afterTurns,
                x => string.Equals(x.TurnId, t.TurnId, StringComparison.Ordinal)
            );
    }

    [Fact(DisplayName = "P007-M2-I3: 压缩后发包首条是桩，不含桩前消息，hint 不落库")]
    public async Task Turn_After_Compact_Sends_Stub_First()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        var preStubNonSystemIds = (await _service.GetSessionMessagesAsync(wsId, sessId))
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("压缩摘要一")
        );
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);

        var turnGateway = new ScriptedCompactGateway("收到，继续。");
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            turnGateway,
            new ToolRegistry(registerBuiltIns: false),
            _storageManager
        );
        var turnResult = await orchestrator.RunAgentTurnAsync(wsId, sessId, "继续");
        Assert.True(turnResult.Success);
        Assert.NotEmpty(turnGateway.Requests);
        var request = turnGateway.Requests[^1];
        Assert.True(request.Messages.Count > 1);
        // 首条为本轮构造 system，其后第一条历史必须是桩
        var stubInRequest = request.Messages.FirstOrDefault(m =>
            string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        Assert.NotNull(stubInRequest);
        int stubIdx = -1;
        for (int si = 0; si < request.Messages.Count; si++)
        {
            if (string.Equals(request.Messages[si].Id, stubInRequest!.Id, StringComparison.Ordinal))
            {
                stubIdx = si;
                break;
            }
        }
        Assert.True(stubIdx >= 1);
        // 桩之前只允许本轮构造 system 与前导 system 行
        for (int i = 1; i < stubIdx; i++)
            Assert.Equal("system", request.Messages[i].Role);
        foreach (var m in request.Messages.Skip(1))
            Assert.DoesNotContain(m.Id, preStubNonSystemIds);
        // 单桩边缘 hint 仅内存态：本轮切片含桩后新问，不应触发；请求内亦无 hint 文案
        Assert.DoesNotContain(
            request.Messages,
            m => (m.Content ?? string.Empty).Contains("以下是此前对话摘要")
        );
        // 内存态 hint 不落库
        var stored = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.DoesNotContain(stored, m => (m.Content ?? string.Empty).Contains("以下是此前对话摘要"));
    }

    [Fact(DisplayName = "P007-M2-I4: 无桩会话发包内容与全量一致（零回归）")]
    public async Task Turn_Without_Stub_Sends_Full_History()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 3);
        var turnGateway = new ScriptedCompactGateway("收到，你好。");
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            turnGateway,
            new ToolRegistry(registerBuiltIns: false),
            _storageManager
        );
        var turnResult = await orchestrator.RunAgentTurnAsync(wsId, sessId, "你好");
        Assert.True(turnResult.Success);
        Assert.NotEmpty(turnGateway.Requests);
        var request = turnGateway.Requests[^1];
        var stored = await _service.GetSessionMessagesAsync(wsId, sessId);
        // 请求 = [本轮构造 system] + 全量历史 + 本轮 user（assistant 尚未落盘）
        var expectedIds = stored.Take(stored.Count - 1).Select(m => m.Id).ToArray();
        var actualIds = request.Messages.Skip(1).Select(m => m.Id).ToArray();
        Assert.Equal(expectedIds, actualIds);
        // 逐字节一致：同一 fixture 序列化后 JSON 比较
        string reqJson = JsonSerializer.Serialize(
            request.Messages.Skip(1).ToList(),
            HaloJsonOptions.CompactCamelCase
        );
        string storedJson = JsonSerializer.Serialize(
            stored.Take(stored.Count - 1).ToList(),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Equal(storedJson, reqJson);
    }

    [Fact(DisplayName = "P007-M2-I5: 多轮压缩接 prevSummary，keepLastGroups 裁剪摘要输入")]
    public async Task Second_Compact_Uses_Prev_Summary_And_Tail_Cap()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 4);
        SetStubMode(_storageManager);
        var compactGateway = new ScriptedCompactGateway("摘要A-独特-111", "摘要B-独特-222");
        var compaction = new CompactionService(_service, _budgetService, compactGateway);
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);
        string? stub1 = (await _service.GetLastCompactStubAsync(wsId, sessId))?.MessageId;
        Assert.False(string.IsNullOrWhiteSpace(stub1));
        for (int i = 0; i < 3; i++)
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
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);
        Assert.Equal(2, compactGateway.Requests.Count);
        string secondBody = compactGateway.Requests[1].Messages[0].Content ?? string.Empty;
        Assert.Contains("上轮上下文摘要", secondBody);
        Assert.Contains("摘要A-独特-111", secondBody);
        var active = await _service.GetActiveContextMessagesAsync(wsId, sessId);
        Assert.True(ActiveContext.IsStub(active[1]));
        string? stub2 = (await _service.GetLastCompactStubAsync(wsId, sessId))?.MessageId;
        Assert.False(string.IsNullOrWhiteSpace(stub2));
        Assert.NotEqual(stub1, stub2);
        Assert.Equal(stub2, active[1].Id);

        // keepLastGroups 上限：长桩后历史仅尾部组进摘要输入
        var (wsId2, sessId2) = await NewSessionAsync();
        await BuildHistoryAsync(wsId2, sessId2, 4);
        var compactGateway2 = new ScriptedCompactGateway("首份摘要", "第二份摘要");
        var compaction2 = new CompactionService(_service, _budgetService, compactGateway2);
        Assert.True((await compaction2.CompactAsync(wsId2, sessId2)).Success);
        for (int i = 0; i < 12; i++)
            await _service.AppendSessionMessageAsync(
                wsId2,
                sessId2,
                UnifiedChatMessage.User($"pair{i:00}")
            );
        Assert.True((await compaction2.CompactAsync(wsId2, sessId2, keepLastGroups: 2)).Success);
        Assert.Equal(2, compactGateway2.Requests.Count);
        string cappedBody = compactGateway2.Requests[1].Messages[0].Content ?? string.Empty;
        Assert.Contains("pair11", cappedBody);
        Assert.DoesNotContain("pair00", cappedBody);
    }

    private sealed class ScriptedCompactGateway : ILlmGatewayClient
    {
        private readonly Queue<string> _summaries;
        public readonly List<UnifiedLlmRequest> Requests = new();

        public ScriptedCompactGateway(params string[] summaries) =>
            _summaries = new Queue<string>(summaries);

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Requests.Add(request);
            await Task.Yield();
            yield return UnifiedStreamEvent.Text(
                _summaries.Count > 0 ? _summaries.Dequeue() : "默认摘要"
            );
            yield return UnifiedStreamEvent.Tokens(100, 20, 0, 120);
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
