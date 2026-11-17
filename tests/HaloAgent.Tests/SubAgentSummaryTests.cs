#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 子代理汇总应回填业务正文而非会话流末尾的状态标记。
/// </summary>
public sealed class SubAgentSummaryTests : IDisposable
{
    private const string ProbeText = "PROBE-OK-7731";

    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly AgentConfigService _agentService;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public SubAgentSummaryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentSubSummary_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentSubSummaryBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _agentService = new AgentConfigService(_storageManager);
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
        _projectDir = Path.Combine(_tempRoot, "Projects", "SubSummaryTest");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "子代理汇总测试项目")
            .GetAwaiter()
            .GetResult();
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

    private AgentOrchestrator BuildOrchestrator(ScriptedSubGateway gateway)
    {
        var policy = new SubPolicySnapshot(_agentService.GetInternal());
        var coordinator = new SubAgentCoordinator(
            _service,
            _budgetService,
            gateway,
            _storageManager,
            null,
            null,
            _agentService,
            null
        );
        return new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            ToolRoleRegistries.ForMainRole(policy),
            _storageManager,
            agentConfigService: _agentService,
            coordinator: coordinator,
            subPolicy: policy,
            agentRole: "main"
        );
    }

    [Fact(DisplayName = "子代理汇总保留业务结论并过滤状态标记")]
    public async Task SummaryKeepsBusinessConclusion()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主");
        var gateway = new ScriptedSubGateway(
            new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(
                new[]
                {
                    new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                        _ => new List<UnifiedStreamEvent>
                        {
                            UnifiedStreamEvent.ToolDone(
                                "call_spawn_probe",
                                "spawn_subagent",
                                "{\"task\":\"读样例并抄下内容，末行写固定串\",\"label\":\"probe\"}"
                            ),
                            UnifiedStreamEvent.Completed(),
                        }
                    ),
                }
            ),
            _ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.Text($"抄写完毕，末行 {ProbeText}。"),
                UnifiedStreamEvent.Completed(),
            }
        );
        var orch = BuildOrchestrator(gateway);
        var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "并行抄写任务");
        Assert.True(result.Success);
        var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
        var spawnMsg = msgs.Find(m =>
            m.Role == "tool" && string.Equals(m.Name, "spawn_subagent", StringComparison.Ordinal)
        );
        Assert.NotNull(spawnMsg);
        string payload = spawnMsg!.Content ?? string.Empty;
        Assert.Contains(ProbeText, payload);
        Assert.DoesNotContain("[SubStatus]", payload);
    }

    [Fact(DisplayName = "子代理汇总在仅状态标记时回退为非空")]
    public async Task SummaryFallbackWhenOnlyMarkers()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主");
        var gateway = new ScriptedSubGateway(
            new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(
                new[]
                {
                    new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                        _ => new List<UnifiedStreamEvent>
                        {
                            UnifiedStreamEvent.ToolDone(
                                "call_spawn_quiet",
                                "spawn_subagent",
                                "{\"task\":\"静默子任务\",\"label\":\"quiet\"}"
                            ),
                            UnifiedStreamEvent.Completed(),
                        }
                    ),
                }
            ),
            _ => new List<UnifiedStreamEvent> { UnifiedStreamEvent.Completed() }
        );
        var orch = BuildOrchestrator(gateway);
        var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "静默子任务");
        Assert.True(result.Success);
        var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
        var spawnMsg = msgs.Find(m =>
            m.Role == "tool" && string.Equals(m.Name, "spawn_subagent", StringComparison.Ordinal)
        );
        Assert.NotNull(spawnMsg);
        string payload = spawnMsg!.Content ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(payload));
        using var doc = JsonDocument.Parse(payload);
        string summary = doc.RootElement.TryGetProperty("summary", out var summaryProp)
            ? summaryProp.GetString() ?? string.Empty
            : string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    private sealed class ScriptedSubGateway : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? _script;
        private readonly Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? _fallback;

        public ScriptedSubGateway(
            Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? script,
            Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? fallback = null
        )
        {
            _script = script;
            _fallback = fallback;
        }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            List<UnifiedStreamEvent> events;
            if (_script != null && _script.Count > 0)
                events = _script.Dequeue()(request);
            else if (_fallback != null)
                events = _fallback(request);
            else
                events = new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.Text("默认回复。"),
                    UnifiedStreamEvent.Completed(),
                };
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
