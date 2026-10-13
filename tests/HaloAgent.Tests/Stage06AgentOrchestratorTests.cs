#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

// 并行禁用由 Stage01FoundationTests.cs 的 [assembly: CollectionBehavior] 全局控制。

/// <summary>
/// Stage 06 Agent 编排验收：SystemPrompt 铁律、ReAct 直答、工具自愈、MaxIterations 截断、取消时效。
/// 全部使用 Fake 网关与 Fake/真实工具适配器，避免真实网络。
/// </summary>
public sealed class Stage06AgentOrchestratorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public Stage06AgentOrchestratorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage06Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage06Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();

        _projectDir = Path.Combine(_tempRoot, "Projects", "AgentTest");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service.AddOrUpdateWorkspaceAsync(_projectDir, "Agent测试项目").GetAwaiter().GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "Stage06 验收会话").GetAwaiter().GetResult();
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

    [Fact]
    public async Task SystemPrompt_Contains_CmdFirst_And_PowerShellBan()
    {
        string prompt = await SystemPromptBuilder.BuildAsync(_workspace, "Python 3.11.9 (C:\\Python311\\python.exe)");

        Assert.Contains("cmd.exe", prompt);
        Assert.Contains("FIRST CHOICE", prompt);
        Assert.Contains("powershell", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AVOID PowerShell", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("edit_file", prompt);
        Assert.Contains("list_dir", prompt);
        Assert.Contains("grep_search", prompt);
        Assert.Contains(_workspace.RootPath, prompt);
        Assert.Contains(_workspace.Name, prompt);
    }

    [Fact]
    public async Task React_NoTools_DirectAnswer_Completes()
    {
        var gateway = new ScriptedFakeGateway(new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(new[]
        {
            new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(_ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.Text("任务已完成，无需调用工具。"),
                UnifiedStreamEvent.Tokens(100, 20, 80, 120),
                UnifiedStreamEvent.Completed(),
            }),
        }));

        var orchestrator = new AgentOrchestrator(_service, _budgetService, gateway, new ToolRegistry(registerBuiltIns: false), _storageManager);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(_workspace.Id, _session.Id, "你好");

        Assert.True(result.Success);
        Assert.False(result.WasCancelled);
        Assert.False(result.WasTruncated);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(0, result.ToolExecutions);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Contains("任务已完成", messages[1].Content);
        Assert.Equal(100, messages[1].InputTokens);
        Assert.Equal(80, messages[1].CachedTokens);
    }

    [Fact]
    public async Task React_ToolSelfHealing_EditMiss_Then_Read_Then_Edit_Succeeds()
    {
        // 真实文件：edit 的 old_string 首次故意未命中（自愈提示回写），Fake LLM 下一轮先 read 再 edit 成功。
        string targetFile = Path.Combine(_projectDir, "notes.txt");
        await File.WriteAllTextAsync(targetFile, "line1\nline2\n");

        var script = new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>();
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone("call_edit_1", "edit_file", "{\"file_path\":\"notes.txt\",\"old_string\":\"not-exist-line\",\"new_string\":\"x\"}"),
            UnifiedStreamEvent.Completed(),
        });
        script.Enqueue(req =>
        {
            // 自愈断言：上一轮 tool 消息必须包含自愈提示（含 read_file 引导）
            var history = _service.GetSessionMessagesAsync(_workspace.Id, _session.Id).GetAwaiter().GetResult();
            var lastTool = history.FindLast(m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(lastTool);
            Assert.Contains("read_file", lastTool!.Content ?? string.Empty);
            return new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.ToolDone("call_read_1", "read_file", "{\"file_path\":\"notes.txt\"}"),
                UnifiedStreamEvent.Completed(),
            };
        });
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone("call_edit_2", "edit_file", "{\"file_path\":\"notes.txt\",\"old_string\":\"line1\",\"new_string\":\"line1-edited\"}"),
            UnifiedStreamEvent.Completed(),
        });
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.Text("已读取并修复文件。"),
            UnifiedStreamEvent.Completed(),
        });

        var gateway = new ScriptedFakeGateway(script);
        var orchestrator = new AgentOrchestrator(_service, _budgetService, gateway, new ToolRegistry(), _storageManager);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(_workspace.Id, _session.Id, "修复 notes.txt");

        Assert.True(result.Success);
        Assert.False(result.WasTruncated);
        Assert.Equal(4, result.Iterations);
        Assert.Equal(3, result.ToolExecutions);
        Assert.Equal("line1-edited\nline2\n", await File.ReadAllTextAsync(targetFile));

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Contains(messages, m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && (m.Content ?? string.Empty).Contains("[Tool Error - edit_file]"));
    }

    [Fact]
    public async Task React_MaxIterations_Truncates()
    {
        var gateway = new ScriptedFakeGateway(null, fallback: req => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone($"call_{Guid.NewGuid():N}", "list_dir", "{\"path\":\".\"}"),
            UnifiedStreamEvent.Completed(),
        });

        var orchestrator = new AgentOrchestrator(
            _service, _budgetService, gateway, new ToolRegistry(), _storageManager, maxIterations: 3);

        Assert.Equal(3, orchestrator.MaxIterations);
        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(_workspace.Id, _session.Id, "列出目录三轮");

        Assert.True(result.Success);
        Assert.True(result.WasTruncated);
        Assert.Equal(3, result.Iterations);
        Assert.Equal(2, result.ToolExecutions); // 第 3 轮触发截断，不再执行工具
    }

    [Fact]
    public async Task React_Cancel_BeforeStart_Under100ms()
    {
        var gateway = new ScriptedFakeGateway(new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>(new[]
        {
            new Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>(_ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.Text("不应执行到这里。"),
                UnifiedStreamEvent.Completed(),
            }),
        }));

        var orchestrator = new AgentOrchestrator(_service, _budgetService, gateway, new ToolRegistry(registerBuiltIns: false), _storageManager);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(_workspace.Id, _session.Id, "取消我", cancellationToken: cts.Token);
        sw.Stop();

        Assert.True(result.WasCancelled);
        Assert.True(sw.ElapsedMilliseconds < 100, $"取消耗时 {sw.ElapsedMilliseconds}ms 超过 100ms");
    }

    // ---- Fake 网关：按脚本逐轮产出事件，无真实网络 ----

    private sealed class ScriptedFakeGateway : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? _script;
        private readonly Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? _fallback;

        public ScriptedFakeGateway(
            Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? script,
            Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? fallback = null)
        {
            _script = script;
            _fallback = fallback;
        }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
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
