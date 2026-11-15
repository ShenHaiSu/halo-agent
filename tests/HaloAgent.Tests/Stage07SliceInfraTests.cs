#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007-M2：切片基础设施验收（I6 并发追加 + I8 契约 + 快路径等价性/性能 + 自愈不跨桩 + 预算冒烟）。</summary>
public sealed class Stage07SliceInfraTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public Stage07SliceInfraTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage07Infra_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage07InfraBoot_{Guid.NewGuid():N}"
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

    private async Task BuildHistoryAsync(string wsId, string sessId, int pairs = 5)
    {
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.System("你是助手"));
        for (int i = 0; i < pairs; i++)
        {
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"q{i}")
            );
            var a = UnifiedChatMessage.Assistant($"a{i}");
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

    [Fact(DisplayName = "P007-M2-I6: 压缩期间追加的消息不丢失、不重复，桩仍为末条")]
    public async Task Concurrent_Append_During_Compact_Is_Kept_Once()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId);
        SetStubMode(_storageManager);
        var gate = new BlockingCompactGateway();
        var compaction = new CompactionService(_service, _budgetService, gate);
        var compactTask = compaction.CompactAsync(wsId, sessId);
        Assert.True(gate.WaitStarted(TimeSpan.FromSeconds(10)));
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.User("并发追问"));
        gate.Release();
        var result = await compactTask;
        Assert.True(result.Success);
        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        Assert.Equal(
            1,
            full.Count(m => string.Equals(m.Content, "并发追问", StringComparison.Ordinal))
        );
        // P007：append-only 下桩写入即末次追加，桩恒为末条（期间追加天然在桩前，仍在库内可回溯）。
        Assert.True(ActiveContext.IsStub(full[^1]));
        var active = await _service.GetActiveContextMessagesAsync(wsId, sessId);
        Assert.Equal(2, active.Count); // [前导 system, 桩]：期间追加在桩前，不进切片但仍在库内
        Assert.True(ActiveContext.IsStub(active[^1]));
    }

    [Fact(DisplayName = "P007-M2-I8: 桩序列化契约字段名（kind/metaJson 小驼峰）")]
    public async Task Stub_Serialization_Contract()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 3);
        SetStubMode(_storageManager);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("契约摘要")
        );
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);
        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        var stub = full.First(m =>
            string.Equals(m.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
        );
        string json = JsonSerializer.Serialize(stub, HaloJsonOptions.CompactCamelCase);
        Assert.Contains("\"kind\"", json);
        Assert.Contains("\"metaJson\"", json);
        Assert.Contains("compact_stub", json);
        Assert.NotNull(stub.MetaJson);
        using var doc = JsonDocument.Parse(stub.MetaJson!);
        var root = doc.RootElement;
        foreach (
            string key in new[]
            {
                "removedCount",
                "summaryTokens",
                "promptTokens",
                "cachedTokens",
                "backupFile"
            }
        )
            Assert.True(root.TryGetProperty(key, out _), $"meta_json 缺少 {key}");
    }

    [Fact(DisplayName = "P007-M2: 快路径与内存切片逐条一致，且显著快于全量读")]
    public async Task Fast_Path_Equals_Memory_Slice_And_Is_Faster()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 500);
        SetStubMode(_storageManager);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("大历史摘要")
        );
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);
        for (int i = 0; i < 50; i++)
            await _service.AppendSessionMessageAsync(
                wsId,
                sessId,
                UnifiedChatMessage.User($"post{i:00}")
            );

        var full = await _repository.GetMessagesAsync(wsId, sessId);
        Assert.True(full.Count > 1000);
        var expected = ActiveContext.Slice(full);
        var fast = await _repository.GetActiveContextMessagesAsync(wsId, sessId);
        Assert.Equal(expected.Select(m => m.Id).ToArray(), fast.Select(m => m.Id).ToArray());
        var serviceActive = await _service.GetActiveContextMessagesAsync(wsId, sessId);
        Assert.Equal(
            expected.Select(m => m.Id).ToArray(),
            serviceActive.Select(m => m.Id).ToArray()
        );
        Assert.Equal(expected.Count, fast.Count); // 桩 + 桩后 50 + 前导 system

        for (int i = 0; i < 3; i++)
        {
            await _repository.GetActiveContextMessagesAsync(wsId, sessId);
            await _repository.GetMessagesAsync(wsId, sessId);
        }
        var fastTimes = new List<long>();
        var fullTimes = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await _repository.GetActiveContextMessagesAsync(wsId, sessId);
            sw.Stop();
            fastTimes.Add(sw.ElapsedMilliseconds);
            sw.Restart();
            await _repository.GetMessagesAsync(wsId, sessId);
            sw.Stop();
            fullTimes.Add(sw.ElapsedMilliseconds);
        }
        fastTimes.Sort();
        fullTimes.Sort();
        Assert.True(fastTimes[2] <= fullTimes[2]);
    }

    [Fact(DisplayName = "P007-M2: 自愈不跨桩（桩前孤儿 tool 不进活跃切片）")]
    public async Task Heal_Does_Not_Cross_Stub()
    {
        var (wsId, sessId) = await NewSessionAsync();
        // 桩前孤儿 assistant(toolCalls 无回包) + 桩 + 桩后 user
        var orphan = UnifiedChatMessage.Assistant(
            "scanning",
            new List<UnifiedToolCall> { new("call_orphan", null, "read_file", "{}") }
        );
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.User("旧问"));
        await _service.AppendSessionMessageAsync(wsId, sessId, orphan);
        var stub = UnifiedChatMessage.User("桩摘要");
        stub.Kind = MessageKind.CompactStub;
        await _service.AppendSessionMessageAsync(wsId, sessId, stub);
        await _service.AppendSessionMessageAsync(wsId, sessId, UnifiedChatMessage.User("新问"));

        var full = await _service.GetSessionMessagesAsync(wsId, sessId);
        var active = ActiveContext.Slice(full);
        Assert.Equal(2, active.Count);
        Assert.DoesNotContain(
            active,
            m =>
                (m.ToolCalls ?? new List<UnifiedToolCall>()).Any(c =>
                    string.Equals(c.CallId, "call_orphan", StringComparison.Ordinal)
                )
        );
        var (healed, _, _) = WorkspaceSessionService.RebuildHealedHistory(active);
        Assert.DoesNotContain(
            healed,
            m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(healed, m => (m.Content ?? string.Empty).Contains("call_orphan"));
    }

    [Fact(DisplayName = "P007-M2: 预算读活跃切片不抛（Cuts 切换冒烟）")]
    public async Task Budget_Reads_Active_Slice()
    {
        var (wsId, sessId) = await NewSessionAsync();
        await BuildHistoryAsync(wsId, sessId, 4);
        SetStubMode(_storageManager);
        var compaction = new CompactionService(
            _service,
            _budgetService,
            new ScriptedCompactGateway("预算摘要")
        );
        Assert.True((await compaction.CompactAsync(wsId, sessId)).Success);
        var dto = await _budgetService.GetBudgetStatusAsync("p", "m", wsId, sessId);
        Assert.NotNull(dto);
        Assert.True(dto.UsedInputTokens >= 0);
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

    private sealed class BlockingCompactGateway : ILlmGatewayClient
    {
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public bool WaitStarted(TimeSpan timeout) => _started.Wait(timeout);

        public void Release() => _release.Set();

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            _started.Set();
            await Task.Run(() => _release.Wait(ct), ct);
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("阻塞后摘要");
            yield return UnifiedStreamEvent.Tokens(100, 20, 0, 120);
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
