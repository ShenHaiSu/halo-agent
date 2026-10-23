#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need13-07 §1.2/1.3：权限拒绝 + 编排（barrier/配额/取消/截断/愈合/混协议）验收。
/// ScriptedFakeGateway 复用 08 范式；零修改老文件。
/// </summary>
public sealed class Stage13SubAgentTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly AgentConfigService _agentService;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public Stage13SubAgentTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage13_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage13Boot_{Guid.NewGuid():N}"
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "SubAgentTest");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "子代理测试项目")
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

    private ToolContext SubCtx() =>
        new()
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = "sess_sub_test",
            DataRoot = _tempRoot,
            AgentRole = "sub",
            SubAllowSkillWrite = false,
            SubAllowMemoryWrite = false,
        };

    // ---------- 权限组 ----------

    [Fact(DisplayName = "S13: 子 skill 写拒")]
    public async Task Sub_SkillWriteDenied()
    {
        var r = await SkillDispatcher.ExecuteKindAsync(
            true,
            new ToolCallDto(
                "c1",
                "skill_tool",
                "{\"action\":\"create\",\"scope\":\"project\",\"name\":\"t1\",\"content\":\"x\"}"
            ),
            SubCtx(),
            CancellationToken.None
        );
        Assert.False(r.Success);
        Assert.Contains("[PERMISSION_DENIED:skill_tool:write]", r.Error);
    }

    [Fact(DisplayName = "S13: 子 skill 读放行")]
    public async Task Sub_SkillReadAllowed()
    {
        var r = await SkillDispatcher.ExecuteKindAsync(
            true,
            new ToolCallDto("c1", "skill_tool", "{\"action\":\"list\",\"scope\":\"project\"}"),
            SubCtx(),
            CancellationToken.None
        );
        Assert.True(r.Success);
    }

    [Fact(DisplayName = "S13: 子 memory 写拒")]
    public async Task Sub_MemoryWriteDenied()
    {
        var r = await SkillDispatcher.ExecuteKindAsync(
            false,
            new ToolCallDto(
                "c1",
                "memory_tool",
                "{\"action\":\"delete\",\"scope\":\"project\",\"name\":\"t1\"}"
            ),
            SubCtx(),
            CancellationToken.None
        );
        Assert.False(r.Success);
        Assert.Contains("[PERMISSION_DENIED:memory_tool:write]", r.Error);
    }

    [Fact(DisplayName = "S13: 子 ask 首行拒且未进 gate")]
    public async Task Sub_AskDenied()
    {
        var tool = new AskUserTool();
        var r = await tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "ask_user",
                "{\"questions\":[{\"id\":\"q1\",\"question\":\"选哪个？\"}]}"
            ),
            SubCtx()
        );
        Assert.False(r.Success);
        Assert.Contains("[SUBAGENT_ASK_DENIED]", r.Error);
        Assert.False(AskUserGate.HasPending("sess_sub_test"));
    }

    [Fact(DisplayName = "S13: 子内 spawn 拒")]
    public async Task Sub_NestedSpawnDenied()
    {
        var tool = new DisabledSpawnTool();
        var r = await tool.ExecuteAsync(
            new ToolCallDto("c1", "spawn_subagent", "{\"task\":\"hi\"}"),
            SubCtx()
        );
        Assert.False(r.Success);
        Assert.Contains("[SUBAGENT_NESTED_DENIED]", r.Error);
    }

    [Fact(DisplayName = "S13: 关闭时主 spawn 拒且未建会话")]
    public async Task Main_SpawnDisabled_NoSession()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主").ConfigureAwait(false);
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 5,
                SubAgentEnabled = false,
            }
        );
        try
        {
            var gateway = new ScriptedFakeGateway13(
                new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(
                    new[]
                    {
                        new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                            _ => new List<UnifiedStreamEvent>
                            {
                                UnifiedStreamEvent.ToolDone(
                                    "call_sp1",
                                    "spawn_subagent",
                                    "{\"task\":\"做点事\"}"
                                ),
                                UnifiedStreamEvent.Completed(),
                            }
                        ),
                        new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                            _ => new List<UnifiedStreamEvent>
                            {
                                UnifiedStreamEvent.Text("已拒绝，转串行完成。"),
                                UnifiedStreamEvent.Completed(),
                            }
                        ),
                    }
                )
            );
            var orch = new AgentOrchestrator(
                _service,
                _budgetService,
                gateway,
                ToolRoleRegistries.ForMainRole(new SubPolicySnapshot()),
                _storageManager,
                agentConfigService: _agentService
            );
            var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "并行做事");
            Assert.True(result.Success);
            var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
            Assert.Contains(
                msgs,
                m => m.Role == "tool" && (m.Content ?? string.Empty).Contains("[SUBAGENT_DISABLED]")
            );
            var subs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
            Assert.Empty(subs);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    // ---------- 编排组 ----------

    private AgentOrchestrator MainOrch(ScriptedFakeGateway13 gw, SubPolicySnapshot? policy = null)
    {
        var p = policy ?? new SubPolicySnapshot(_agentService.GetInternal());
        var coord = new SubAgentCoordinator(
            _service,
            _budgetService,
            gw,
            _storageManager,
            null,
            null,
            _agentService,
            null
        );
        return new AgentOrchestrator(
            _service,
            _budgetService,
            gw,
            ToolRoleRegistries.ForMainRole(p),
            _storageManager,
            agentConfigService: _agentService,
            coordinator: coord,
            subPolicy: p,
            agentRole: "main"
        );
    }

    [Fact(DisplayName = "S13: 3 子 barrier 收齐才进下一轮")]
    public async Task Barrier_CollectsAll()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主").ConfigureAwait(false);
        var script = new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>();
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone(
                "c_sp1",
                "spawn_subagent",
                "{\"task\":\"任务一\",\"label\":\"t1\"}"
            ),
            UnifiedStreamEvent.ToolDone(
                "c_sp2",
                "spawn_subagent",
                "{\"task\":\"任务二\",\"label\":\"t2\"}"
            ),
            UnifiedStreamEvent.ToolDone(
                "c_sp3",
                "spawn_subagent",
                "{\"task\":\"任务三\",\"label\":\"t3\"}"
            ),
            UnifiedStreamEvent.Completed(),
        });
        // 子轮：每子 1 轮 tool 后纯文本总结（fallback 覆盖所有子请求）
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.Text("子总结：完成。"),
            UnifiedStreamEvent.Completed(),
        });
        var gateway = new ScriptedFakeGateway13(
            script,
            fallback: _ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.Text("子总结：完成。"),
                UnifiedStreamEvent.Completed(),
            }
        );
        // 主第二轮：看到回包后纯文本收尾
        var gateway2Queue = script;
        var orch = MainOrch(gateway);
        // 主首轮 spawn 后 barrier，子跑完后主需第二轮 LLM：把 fallback 换成主收尾
        var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "并行三任务");
        Assert.True(result.Success);
        var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
        int spawnTools = 0;
        foreach (var m in msgs)
            if (
                m.Role == "tool"
                && m.Name == "spawn_subagent"
                && (m.Content ?? string.Empty).Contains("\"success\":true")
            )
                spawnTools++;
        Assert.Equal(3, spawnTools);
        var subs = await _service.GetSubSessionsAsync(_workspace.Id, main.Id);
        Assert.Equal(3, subs.Count);
    }

    [Fact(DisplayName = "S13: 超限部分拒，先到者跑")]
    public async Task Quota_PartialReject()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主").ConfigureAwait(false);
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 5,
                SubAgentEnabled = true,
                SubMaxParallel = 2,
                SubMaxIterations = 5,
            }
        );
        try
        {
            var gateway = new ScriptedFakeGateway13(
                new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(
                    new[]
                    {
                        new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                            _ => new List<UnifiedStreamEvent>
                            {
                                UnifiedStreamEvent.ToolDone(
                                    "c1",
                                    "spawn_subagent",
                                    "{\"task\":\"t1\"}"
                                ),
                                UnifiedStreamEvent.ToolDone(
                                    "c2",
                                    "spawn_subagent",
                                    "{\"task\":\"t2\"}"
                                ),
                                UnifiedStreamEvent.ToolDone(
                                    "c3",
                                    "spawn_subagent",
                                    "{\"task\":\"t3\"}"
                                ),
                                UnifiedStreamEvent.ToolDone(
                                    "c4",
                                    "spawn_subagent",
                                    "{\"task\":\"t4\"}"
                                ),
                                UnifiedStreamEvent.Completed(),
                            }
                        ),
                    }
                ),
                fallback: _ => new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.Text("子总结。"),
                    UnifiedStreamEvent.Completed(),
                }
            );
            var orch = MainOrch(gateway);
            var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "四个任务");
            Assert.True(result.Success);
            var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
            int ok = 0,
                quota = 0;
            foreach (var m in msgs)
            {
                if (m.Role != "tool" || m.Name != "spawn_subagent")
                    continue;
                if ((m.Content ?? string.Empty).Contains("[SUBAGENT_QUOTA_EXCEEDED]"))
                    quota++;
                else if ((m.Content ?? string.Empty).Contains("\"success\":true"))
                    ok++;
            }
            Assert.Equal(2, ok);
            Assert.Equal(2, quota);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "S13: 子截断回填 success=false")]
    public async Task SubTruncated_Bubbled()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主").ConfigureAwait(false);
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 5,
                SubAgentEnabled = true,
                SubMaxParallel = 3,
                SubMaxIterations = 1,
            }
        );
        try
        {
            var gateway = new ScriptedFakeGateway13(
                new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(
                    new[]
                    {
                        new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(
                            _ => new List<UnifiedStreamEvent>
                            {
                                UnifiedStreamEvent.ToolDone(
                                    "c1",
                                    "spawn_subagent",
                                    "{\"task\":\"多步任务\"}"
                                ),
                                UnifiedStreamEvent.Completed(),
                            }
                        ),
                    }
                ),
                fallback: _ => new List<UnifiedStreamEvent>
                {
                    // 子首轮仍发 toolcall，但子 maxIterations=1 → 截断
                    UnifiedStreamEvent.ToolDone("csub", "list_dir", "{\"path\":\".\"}"),
                    UnifiedStreamEvent.Completed(),
                }
            );
            var orch = MainOrch(gateway);
            var result = await orch.RunAgentTurnAsync(_workspace.Id, main.Id, "多步子任务");
            Assert.True(result.Success);
            var msgs = await _service.GetSessionMessagesAsync(_workspace.Id, main.Id);
            Assert.Contains(
                msgs,
                m =>
                    m.Role == "tool"
                    && m.Name == "spawn_subagent"
                    && (m.Content ?? string.Empty).Contains("\"success\":false")
            );
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "S13: 含 ItemId 孤儿历史愈合通过")]
    public async Task Heal_WithItemId()
    {
        var main = await _service.CreateSessionAsync(_workspace.Id, "主").ConfigureAwait(false);
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            main.Id,
            UnifiedChatMessage.Assistant(
                "half",
                new List<UnifiedToolCall> { new("call_x", "fc_x", "list_dir", "{}") }
            )
        );
        var r = await _service.HealSessionHistoryAsync(_workspace.Id, main.Id);
        Assert.True(r.Success);
    }

    // ---- Fake 网关 ----

    private sealed class ScriptedFakeGateway13 : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? _script;
        private readonly Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? _fallback;

        public ScriptedFakeGateway13(
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
                    UnifiedStreamEvent.Text("fake 默认回复。"),
                    UnifiedStreamEvent.Completed()
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
