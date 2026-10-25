#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// need08-04 §1：智能体行为配置（agent.json + 双无限语义）验收。
/// 对标 Stage06AgentOrchestratorTests + Stage05ToolExecutionTests(AC03)，零修改老文件。
/// </summary>
public sealed class Stage08AgentConfigTests : IDisposable
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
    private readonly SessionMetadata _session;

    public Stage08AgentConfigTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage08Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage08Bootstrap_{Guid.NewGuid():N}"
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

        _projectDir = Path.Combine(_tempRoot, "Projects", "AgentCfgTest");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "Agent配置测试项目")
            .GetAwaiter()
            .GetResult();
        _session = _service
            .CreateSessionAsync(_workspace.Id, "Stage08 验收会话")
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
        catch
        {
            // Ignore cleanup failures in test temp folders
        }
    }

    [Fact(DisplayName = "S08: 缺省 agent.json 自动生成且三默认值正确")]
    public void Defaults_CreateAndLoad()
    {
        string path = _agentService.AgentConfigFilePath;
        if (File.Exists(path))
            File.Delete(path);
        var (doc, isDefault, _) = _agentService.LoadWithMeta();
        Assert.True(File.Exists(path));
        Assert.Equal(30000, doc.ExecDefaultTimeoutMs);
        Assert.Equal(300000, doc.ExecMaxTimeoutMs);
        Assert.Equal(25, doc.MaxIterations);
        Assert.True(isDefault);
    }

    [Fact(DisplayName = "S08: 负数保存均被拒绝且 field 定位正确")]
    public void Validation_NegativeRejected()
    {
        var exD = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = -1,
                        ExecMaxTimeoutMs = 300000,
                        MaxIterations = 25
                    }
                )
        );
        Assert.Equal("execDefaultTimeoutMs", exD.Field);

        var exM = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 30000,
                        ExecMaxTimeoutMs = -5,
                        MaxIterations = 25
                    }
                )
        );
        Assert.Equal("execMaxTimeoutMs", exM.Field);

        var exN = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 30000,
                        ExecMaxTimeoutMs = 300000,
                        MaxIterations = -1
                    }
                )
        );
        Assert.Equal("maxIterations", exN.Field);
    }

    [Fact(DisplayName = "S08: 超硬顶保存被拒绝")]
    public void Validation_CeilRejected()
    {
        var exD = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 3600001,
                        ExecMaxTimeoutMs = 3600001,
                        MaxIterations = 25
                    }
                )
        );
        Assert.Equal("execDefaultTimeoutMs", exD.Field);

        var exN = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 30000,
                        ExecMaxTimeoutMs = 300000,
                        MaxIterations = 501
                    }
                )
        );
        Assert.Equal("maxIterations", exN.Field);
    }

    [Fact(DisplayName = "S08: D>M 被拒绝且 msg 含指引")]
    public void Validation_DefaultGtMaxRejected()
    {
        var ex = Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 300000,
                        ExecMaxTimeoutMs = 60000,
                        MaxIterations = 25
                    }
                )
        );
        Assert.Equal("execDefaultTimeoutMs", ex.Field);
        Assert.Contains("默认超时不得大于上限", ex.Message);
    }

    [Fact(DisplayName = "S08: 三项 0 均可保存落盘")]
    public void Validation_ZeroAccepted()
    {
        var saved = _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 0,
                ExecMaxTimeoutMs = 0,
                MaxIterations = 0
            }
        );
        Assert.Equal(0, saved.ExecDefaultTimeoutMs);
        Assert.Equal(0, saved.ExecMaxTimeoutMs);
        Assert.Equal(0, saved.MaxIterations);
        var reloaded = _agentService.GetInternal();
        Assert.Equal(0, reloaded.ExecDefaultTimeoutMs);
        Assert.Equal(0, reloaded.MaxIterations);
        // 恢复默认，避免污染后续用例
        _agentService.ResetAgentConfig(null);
    }

    [Fact(DisplayName = "S08: 按项恢复只回该项")]
    public void Reset_FieldMatrix()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 60000,
                ExecMaxTimeoutMs = 600000,
                MaxIterations = 50
            }
        );

        var r1 = _agentService.ResetAgentConfig("defaultTimeout");
        Assert.Equal(30000, r1.ExecDefaultTimeoutMs);
        Assert.Equal(600000, r1.ExecMaxTimeoutMs);
        Assert.Equal(50, r1.MaxIterations);

        var r2 = _agentService.ResetAgentConfig("maxTimeout");
        Assert.Equal(30000, r2.ExecDefaultTimeoutMs);
        Assert.Equal(300000, r2.ExecMaxTimeoutMs);
        Assert.Equal(50, r2.MaxIterations);

        var r3 = _agentService.ResetAgentConfig("iterations");
        Assert.Equal(30000, r3.ExecDefaultTimeoutMs);
        Assert.Equal(300000, r3.ExecMaxTimeoutMs);
        Assert.Equal(25, r3.MaxIterations);
    }

    [Fact(DisplayName = "S08: 全部恢复与未知项拒绝")]
    public void Reset_AllAndUnknown()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 60000,
                ExecMaxTimeoutMs = 600000,
                MaxIterations = 50
            }
        );
        var all = _agentService.ResetAgentConfig(null);
        Assert.Equal(30000, all.ExecDefaultTimeoutMs);
        Assert.Equal(300000, all.ExecMaxTimeoutMs);
        Assert.Equal(25, all.MaxIterations);

        var all2 = _agentService.ResetAgentConfig("all");
        Assert.Equal(30000, all2.ExecDefaultTimeoutMs);

        var ex = Assert.Throws<AgentConfigValidationException>(
            () => _agentService.ResetAgentConfig("bogus")
        );
        Assert.Equal("field", ex.Field);
        Assert.Contains("未知恢复项", ex.Message);
    }

    [Theory(DisplayName = "S08: 01 §4.1 超时矩阵逐行")]
    [InlineData(null, 30000, 300000, 30000, false)] // T 缺省 → D
    [InlineData(-5, 30000, 300000, 30000, false)] // T<0 视为缺省
    [InlineData(0, 30000, 300000, 0, true)] // T=0 本 call 无限
    [InlineData(300000, 60000, 60000 + 0, 60000, false)] // T>0, M>0 → min（M=60000）
    [InlineData(300000, 30000, 0, 300000, false)] // T>0, M=0 上限关闭不钳制
    [InlineData(0, 60000, 300000, 0, true)] // T=0 显式无限优先于 M
    [InlineData(null, 0, 300000, 0, true)] // D=0 且 T 缺省 → 无限
    public void Resolve_TimeoutMatrix(
        int? requested,
        int cfgD,
        int cfgM,
        int expectedMs,
        bool expectedInfinite
    )
    {
        var (ms, infinite) = AgentTimeoutResolver.Resolve(requested, cfgD, cfgM);
        Assert.Equal(expectedMs, ms);
        Assert.Equal(expectedInfinite, infinite);
    }

    [Fact(DisplayName = "S08: 损坏回退默认+corrupt 文件")]
    public void Corrupt_FallsBack()
    {
        _agentService.EnsureInitialized();
        File.WriteAllText(_agentService.AgentConfigFilePath, "{{{not-json###");
        var (doc, isDefault, fallback) = _agentService.LoadWithMeta();
        Assert.True(isDefault);
        Assert.Equal(30000, doc.ExecDefaultTimeoutMs);
        Assert.Equal(25, doc.MaxIterations);
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        string? dir = Path.GetDirectoryName(_agentService.AgentConfigFilePath);
        Assert.NotNull(dir);
        Assert.NotEmpty(Directory.GetFiles(dir!, "agent.json.corrupt-*"));
        // 恢复默认
        File.Delete(_agentService.AgentConfigFilePath);
        _agentService.EnsureInitialized();
    }

    [Fact(DisplayName = "S08: 两次保存后 .bak 等于第一次值")]
    public void Save_AtomicBak()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 60000,
                ExecMaxTimeoutMs = 600000,
                MaxIterations = 50
            }
        );
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 120000,
                ExecMaxTimeoutMs = 600000,
                MaxIterations = 50
            }
        );
        string bak = _agentService.AgentConfigFilePath + ".bak";
        Assert.True(File.Exists(bak));
        string raw = File.ReadAllText(bak);
        Assert.Contains("60000", raw);
    }

    [Fact(DisplayName = "S08: T=0 非法直接拒绝 INVALID_TIMEOUT（need22-P0 S2：删除无限语义）")]
    public async Task Exec_ZeroTimeout_Rejected()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        var registry = new ToolRegistry();
        var ctx = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
            DataRoot = _tempRoot,
            ExecDefaultTimeoutMsOverride = 60000,
            ExecMaxTimeoutMsOverride = 300000,
        };
        var result = await registry.DispatchAsync(
            new ToolCallDto(
                "call_s08_inf",
                "exec_command",
                "{\"command\": \"ping 127.0.0.1 -n 6\", \"timeout_ms\": 0}"
            ),
            ctx
        );
        Assert.False(result.Success);
        Assert.Equal("INVALID_TIMEOUT", result.ErrorCode);
        Assert.StartsWith("INVALID_TIMEOUT", result.Error);
    }

    [Fact(DisplayName = "S08: 长命令 1s 后取消即停且杀树（need22-P0 S2：有限超时+取消）")]
    public async Task Exec_Cancellable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        var registry = new ToolRegistry();
        var ctx = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
            DataRoot = _tempRoot,
            ExecDefaultTimeoutMsOverride = 60000,
            ExecMaxTimeoutMsOverride = 300000,
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(1000);
        var sw = Stopwatch.StartNew();
        var result = await registry.DispatchAsync(
            new ToolCallDto(
                "call_s08_infcancel",
                "exec_command",
                "{\"command\": \"ping 127.0.0.1 -n 60\", \"timeout_ms\": 60000}"
            ),
            ctx,
            cts.Token
        );
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 30000, $"取消回收过慢：{sw.ElapsedMilliseconds}ms");
        Assert.False(result.Success);
        Assert.Contains("取消", result.Error ?? string.Empty);
    }

    [Fact(DisplayName = "S08: N=0 无限轮次不截断，取消即停")]
    public async Task Iter_Infinite_NoTruncate()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 0
            }
        );
        try
        {
            var gateway = new ScriptedFakeGatewayFor08(
                null,
                fallback: _ => new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.ToolDone(
                        $"call_{Guid.NewGuid():N}",
                        "list_dir",
                        "{\"path\":\".\"}"
                    ),
                    UnifiedStreamEvent.Completed(),
                }
            );
            var toolDone = 0;
            using var cts = new CancellationTokenSource();
            // 第 3 次工具执行完成即取消（ToolExecutionCompleted 事件驱动）。
            // 注：原 CancelAfter(15000) 是“无限轮次跑满 15s 再停”，纯烧时间。
            // 无限模式>=3 轮 / 取消即停 / 不截断的断言都不变，只是提前停，测试作用不变。
            var cancelOnToolDone = new CancelOnToolDoneSink(() =>
            {
                if (Interlocked.Increment(ref toolDone) >= 3)
                    cts.Cancel();
            });
            var orchestrator = new AgentOrchestrator(
                _service,
                _budgetService,
                gateway,
                new ToolRegistry(),
                _storageManager,
                agentConfigService: _agentService,
                sink: cancelOnToolDone
            );
            AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
                _workspace.Id,
                _session.Id,
                "无限轮次任务",
                cancellationToken: cts.Token
            );
            Assert.True(result.WasCancelled);
            Assert.False(result.WasTruncated);
            Assert.True(result.Iterations >= 3, $"无限模式应跑多轮，实际 {result.Iterations}");
            Assert.Equal(0, orchestrator.ResolvedMaxIterations);
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "S08: N=3 有限轮次第 3 轮截断")]
    public async Task Iter_Finite_Truncates()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 3
            }
        );
        try
        {
            var gateway = new ScriptedFakeGatewayFor08(
                null,
                fallback: _ => new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.ToolDone(
                        $"call_{Guid.NewGuid():N}",
                        "list_dir",
                        "{\"path\":\".\"}"
                    ),
                    UnifiedStreamEvent.Completed(),
                }
            );
            var orchestrator = new AgentOrchestrator(
                _service,
                _budgetService,
                gateway,
                new ToolRegistry(),
                _storageManager,
                agentConfigService: _agentService
            );
            AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
                _workspace.Id,
                _session.Id,
                "有限三轮任务"
            );
            Assert.True(result.Success);
            Assert.True(result.WasTruncated);
            Assert.Equal(3, result.Iterations);
            Assert.Equal(2, result.ToolExecutions);
            var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
            Assert.Contains(messages, m => (m.Content ?? string.Empty).Contains("最大迭代次数"));
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    // ---- Fake 网关：按脚本逐轮产出事件，无真实网络 ----

    // 事件驱动取消用 sink：第 3 次 ToolExecutionCompleted 即触发取消回调。
    // 只转发计数，不吞事件，生产行为不变。
    private sealed class CancelOnToolDoneSink : HaloAgent.Services.Agent.IAgentEventSink
    {
        private readonly Action _onToolDone;

        public CancelOnToolDoneSink(Action onToolDone) => _onToolDone = onToolDone;

        public Task EmitAsync(string sessionId, string kind, object? payload)
        {
            if (string.Equals(kind, "ToolExecutionCompleted", StringComparison.Ordinal))
            {
                try
                {
                    _onToolDone();
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        public void EmitStreamDelta(string sessionId, string kind, string textDelta) { }
    }

    private sealed class ScriptedFakeGatewayFor08 : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? _script;
        private readonly Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? _fallback;

        public ScriptedFakeGatewayFor08(
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
            {
                events = _script.Dequeue()(request);
            }
            else if (_fallback != null)
            {
                events = _fallback(request);
            }
            else
            {
                events = new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.Text("fake 默认回复。"),
                    UnifiedStreamEvent.Completed(),
                };
            }

            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
