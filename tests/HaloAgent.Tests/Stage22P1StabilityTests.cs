#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

// 并行禁用由 Stage01FoundationTests.cs 的 [assembly: CollectionBehavior] 全局控制。

/// <summary>
/// need22-P1 T1–T7 稳定性验收（对应 02c B1–B7）：重入/取消释放/超时重试/Barrier/压缩/内存事件/用量。
/// 全部使用 Fake 网关与受控 Handler，避免真实网络；静态 JsEventDispatcher 用前后 ClearForTests 隔离。
/// </summary>
public sealed class Stage22P1StabilityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly AgentConfigService _agentService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;

    public Stage22P1StabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage22Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage22Bootstrap_{Guid.NewGuid():N}"
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
        _budgetService = new TokenBudgetService();
        _agentService = new AgentConfigService(_storageManager);
        _projectDir = Path.Combine(_tempRoot, "Projects", "P1Test");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "P1测试项目")
            .GetAwaiter()
            .GetResult();
        JsEventDispatcher.ClearForTests();
    }

    public void Dispose()
    {
        try
        {
            JsEventDispatcher.ClearForTests();
        }
        catch { }
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    private static void WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
                return;
            Thread.Sleep(100);
        }
    }

    // ---------- B1/T1：重入返回 TURN_IN_PROGRESS + requestId 去重 ----------

    [Fact(DisplayName = "P1-B1: 同 session 重入返回 TURN_IN_PROGRESS（附 activeRequestId），旧轮不受影响正常完成")]
    public async Task T1_Reentry_ReturnsTurnInProgress_OldTurnCompletes()
    {
        var gate = new P1GateGateway();
        var facade = new HostBridgeFacade(
            mainForm: null,
            storageManager: _storageManager,
            llmGatewayClient: gate,
            workspaceSessionService: _service
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-B1 会话");

        try
        {
            var r1 = await facade.StartAgentTurn(
                _workspace.Id,
                session.Id,
                "第一轮",
                requestId: "req-active-1"
            );
            Assert.True(r1.Ok);
            WaitFor(() => gate.Calls >= 1);
            Assert.Equal(1, facade.ActiveTurnCountForTests);

            var r2 = await facade.StartAgentTurn(
                _workspace.Id,
                session.Id,
                "第二轮",
                requestId: "req-active-2"
            );
            Assert.False(r2.Ok);
            Assert.Equal("TURN_IN_PROGRESS", r2.Code);
            Assert.Contains("req-active-1", r2.Message ?? string.Empty);

            // 同 requestId 重复投递 → 去重回显成功，不重起一轮。
            var rdup = await facade.StartAgentTurn(
                _workspace.Id,
                session.Id,
                "第一轮",
                requestId: "req-active-1"
            );
            Assert.True(rdup.Ok);
            Assert.Equal(1, facade.ActiveTurnCountForTests);

            gate.Release();
            WaitFor(() => facade.ActiveTurnCountForTests == 0);

            // 旧轮正常完成：只落了第一轮的 user+assistant，无第二轮 user（无静默丢轮）。
            var messages = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Equal("assistant", messages[1].Role);
            Assert.Contains("gate 放行", messages[1].Content ?? string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- B2/T2：释放 ----------

    [Fact(DisplayName = "P1-B2: 轮结束后活动 token 归零，新 requestId 可正常再发轮")]
    public async Task T2_TurnTokens_Released_After_Turn()
    {
        var gate = new P1GateGateway();
        gate.Release();
        var facade = new HostBridgeFacade(
            mainForm: null,
            storageManager: _storageManager,
            llmGatewayClient: gate,
            workspaceSessionService: _service
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-B2 会话");

        var r1 = await facade.StartAgentTurn(
            _workspace.Id,
            session.Id,
            "第一轮",
            requestId: "req-b2-1"
        );
        Assert.True(r1.Ok);
        WaitFor(() => facade.ActiveTurnCountForTests == 0);
        Assert.Equal(0, facade.ActiveTurnCountForTests);

        var r2 = await facade.StartAgentTurn(
            _workspace.Id,
            session.Id,
            "第二轮",
            requestId: "req-b2-2"
        );
        Assert.True(r2.Ok);
        WaitFor(() => facade.ActiveTurnCountForTests == 0);
        Assert.Equal(0, facade.ActiveTurnCountForTests);
    }

    // ---------- B3/T3：总体超时 ----------

    [Fact(DisplayName = "P1-B3: LLM hung 在 turnTimeoutMs 内终轮并报 TIMEOUT")]
    public async Task T3_TurnTimeout_Ends_Hung_Turn()
    {
        _agentService.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 5,
                TurnTimeoutMs = 1500,
            }
        );
        try
        {
            var hung = new P1HungGateway();
            var orchestrator = new AgentOrchestrator(
                _service,
                _budgetService,
                hung,
                new ToolRegistry(),
                _storageManager,
                agentConfigService: _agentService
            );
            var session = await _service.CreateSessionAsync(_workspace.Id, "P1-B3 会话");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
                _workspace.Id,
                session.Id,
                "hung 任务"
            );
            sw.Stop();
            Assert.False(result.Success);
            Assert.Equal("TIMEOUT", result.ErrorCode);
            Assert.True(
                sw.ElapsedMilliseconds < 30000,
                $"总体超时应在预算内终轮，实际 {sw.ElapsedMilliseconds}ms"
            );
        }
        finally
        {
            _agentService.ResetAgentConfig(null);
        }
    }

    [Fact(DisplayName = "P1-T3: turnTimeoutMs 非法值保存时拒绝（配置项 config/agent.json）")]
    public void T3_TurnTimeout_Config_Validation()
    {
        Assert.Throws<AgentConfigValidationException>(
            () =>
                _agentService.SaveAgentConfig(
                    new AgentConfigDocument
                    {
                        ExecDefaultTimeoutMs = 30000,
                        ExecMaxTimeoutMs = 300000,
                        MaxIterations = 5,
                        TurnTimeoutMs = 500,
                    }
                )
        );
        var def = _agentService.ResetAgentConfig("turnTimeout");
        Assert.Equal(Host.Desktop.Core.Models.AgentDefaults.TurnTimeoutMs, def.TurnTimeoutMs);
    }

    [Fact(DisplayName = "P1-T3: HttpClient.Timeout 上限 120s")]
    public void T3_HttpClient_Timeout_Is_120s()
    {
        using var client = new HaloHttpClientFactory().CreateClient();
        Assert.Equal(TimeSpan.FromSeconds(120), client.Timeout);
    }

    // ---------- T3：空工具名 + 连续同错 ----------

    [Fact(DisplayName = "P1-T3: 空 FunctionName 报 EMPTY_TOOL_NAME，连续 3 次提前截断")]
    public async Task T3_EmptyToolName_ThreeStrikes_Ends_Turn()
    {
        var gateway = new P1ScriptedGateway(
            null,
            _ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.ToolDone($"call_empty_{Guid.NewGuid():N}", "", "{}"),
                UnifiedStreamEvent.Completed(),
            }
        );
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(registerBuiltIns: false),
            _storageManager
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-EMPTY 会话");

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "空名任务"
        );
        // 提前截断暂停（与 maxIterations 截断同契约：Success=true，可“继续”）；逐次 EMPTY_TOOL_NAME 回包可审计。
        Assert.True(result.Success);
        Assert.True(result.WasTruncated);
        Assert.Equal(3, result.Iterations);
        var emptyMsgs = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
        Assert.Contains(
            emptyMsgs,
            m =>
                string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && (m.Content ?? string.Empty).Contains("EMPTY_TOOL_NAME")
        );
    }

    [Fact(DisplayName = "P1-T3: 同一工具错误连续 3 次提前截断（防空转）")]
    public async Task T3_SameToolError_ThreeStrikes_Ends_Turn()
    {
        var gateway = new P1ScriptedGateway(
            null,
            _ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.ToolDone(
                    $"call_missing_{Guid.NewGuid():N}",
                    "read_file",
                    "{\"file_path\":\"不存在的文件.txt\"}"
                ),
                UnifiedStreamEvent.Completed(),
            }
        );
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(),
            _storageManager
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-3STRIKE 会话");

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "读缺失文件"
        );
        // 提前截断暂停（与 maxIterations 截断同契约）：3 轮而非跑满 25 轮。
        Assert.True(result.Success);
        Assert.True(result.WasTruncated);
        Assert.Equal(3, result.Iterations);
    }

    // ---------- B4/T3：429/5xx 退避 ----------

    [Fact(DisplayName = "P1-B4a: 429 两次后成功共计 3 次调用")]
    public async Task T3_Gateway_Retry_429_Twice_Then_Success()
    {
        var origDelay = LlmGatewayClient.RetryDelayAsync;
        LlmGatewayClient.RetryDelayAsync = (_, _) => Task.CompletedTask;
        try
        {
            string okSse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\ndata: [DONE]\n\n";
            var handler = new P1CountingHandler(
                new Func<HttpRequestMessage, HttpResponseMessage>[]
                {
                    _ => new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"rate limited\"}}")
                    },
                    _ => new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"rate limited\"}}")
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(okSse, Encoding.UTF8, "text/event-stream")
                    },
                }
            );
            using var http = new HttpClient(handler);
            var gateway = new LlmGatewayClient(_storageManager, customHttpClient: http);
            var events = new List<UnifiedStreamEvent>();
            await foreach (var evt in gateway.StreamAsync(NewLlmRequest()))
                events.Add(evt);
            Assert.Equal(3, handler.CallCount);
            Assert.Contains(
                events,
                e => e.Kind == UnifiedStreamEventKind.TextDelta && e.TextDelta == "hi"
            );
        }
        finally
        {
            LlmGatewayClient.RetryDelayAsync = origDelay;
        }
    }

    [Fact(DisplayName = "P1-B4b: 持续 429 四次后明确失败并透传 HTTP_429")]
    public async Task T3_Gateway_Retry_429_Persistent_Fails_With_Code()
    {
        var origDelay = LlmGatewayClient.RetryDelayAsync;
        LlmGatewayClient.RetryDelayAsync = (_, _) => Task.CompletedTask;
        try
        {
            var handler = new P1CountingHandler(
                Enumerable
                    .Range(0, 20)
                    .Select<int, Func<HttpRequestMessage, HttpResponseMessage>>(_ =>
                        _ => new HttpResponseMessage((HttpStatusCode)429)
                        {
                            Content = new StringContent(
                                "{\"error\":{\"message\":\"rate limited\"}}"
                            )
                        }
                    )
            );
            using var http = new HttpClient(handler);
            var gateway = new LlmGatewayClient(_storageManager, customHttpClient: http);
            var events = new List<UnifiedStreamEvent>();
            await foreach (var evt in gateway.StreamAsync(NewLlmRequest()))
                events.Add(evt);
            Assert.Equal(11, handler.CallCount);
            Assert.Contains(
                events,
                e => e.Kind == UnifiedStreamEventKind.StreamError && e.ErrorCode == "HTTP_429"
            );
        }
        finally
        {
            LlmGatewayClient.RetryDelayAsync = origDelay;
        }
    }

    // ---------- B3/T4：Barrier 超时 ----------

    [Fact(DisplayName = "P1-B3/T4: 子卡死在预算内截断并标记 timedOut，主轮继续")]
    public async Task T4_Barrier_Timeout_Marks_TimedOut()
    {
        var coord = new SubAgentCoordinator(
            _service,
            _budgetService,
            new P1HungGateway(),
            _storageManager,
            null,
            null,
            _agentService,
            null
        );
        var main = await _service.CreateSessionAsync(_workspace.Id, "P1-T4 主会话");
        var hungHandle = new SubHandle
        {
            SubSessionId = "sub-hung",
            CallId = "call-hung",
            Label = "卡死子",
            Task = new TaskCompletionSource<AgentTurnResult>().Task,
            Cts = new CancellationTokenSource(),
            StartedAtUtc = DateTime.UtcNow,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await coord.BarrierAsync(
            _workspace.Id,
            main.Id,
            new[] { hungHandle },
            Array.Empty<(string, string)>(),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(300)
        );
        sw.Stop();
        Assert.Single(results);
        Assert.True(results[0].TimedOut);
        Assert.Equal("SUB_TIMEOUT", results[0].ErrorCode);
        Assert.True(
            sw.ElapsedMilliseconds < 15000,
            $"Barrier 应在预算内返回，实际 {sw.ElapsedMilliseconds}ms"
        );
        Assert.Contains("timedOut", results[0].ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- B5/T5：压缩 ----------

    [Fact(DisplayName = "P1-B5: 压缩中查询可用、新轮返回 COMPACTING、可取消")]
    public async Task T5_Compact_Queryable_Rejects_New_Turn_Cancellable()
    {
        var hung = new P1HungGateway();
        var facade = new HostBridgeFacade(
            mainForm: null,
            storageManager: _storageManager,
            llmGatewayClient: hung,
            workspaceSessionService: _service
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-B5 会话");
        for (int i = 0; i < 5; i++)
        {
            await _service.AppendSessionMessageAsync(
                _workspace.Id,
                session.Id,
                UnifiedChatMessage.User($"第{i}个用户问题")
            );
            await _service.AppendSessionMessageAsync(
                _workspace.Id,
                session.Id,
                UnifiedChatMessage.Assistant($"第{i}个助手回答")
            );
        }

        var compactRes = await facade.CompactSession(_workspace.Id, session.Id);
        Assert.True(compactRes.Ok);
        WaitFor(() => facade.IsCompactingForTests(session.Id));
        Assert.True(facade.IsCompactingForTests(session.Id));

        // 压缩中查询可用
        var during = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
        Assert.Equal(10, during.Count);

        // 新轮被拒且有码
        var turnRes = await facade.StartAgentTurn(
            _workspace.Id,
            session.Id,
            "新轮",
            requestId: "req-b5-new"
        );
        Assert.False(turnRes.Ok);
        Assert.Equal("COMPACTING", turnRes.Code);

        // 可取消
        var cancelRes = await facade.CancelCompaction(session.Id);
        Assert.True(cancelRes.Ok);
        Assert.True(cancelRes.Data);
        WaitFor(() => !facade.IsCompactingForTests(session.Id));
        Assert.False(facade.IsCompactingForTests(session.Id));
    }

    // ---------- B6/T6：内存与事件 ----------

    [Fact(DisplayName = "P1-B6a: 大 toolOutput>64KB 落盘 fileRef，只留前 4K 预览")]
    public void T6_LargeToolOutput_Spills_To_FileRef()
    {
        string big = new string('x', 100 * 1024);
        string spilled = AgentOrchestrator.SpillLargeToolOutput(
            big,
            _tempRoot,
            "w1",
            "s1",
            "call_big"
        );
        Assert.Contains("[fileRef", spilled);
        Assert.Contains("size=", spilled);
        Assert.True(spilled.Length < 16 * 1024, $"预览应远小于原文，实际 {spilled.Length}");
        int pathStart = spilled.IndexOf("path=\"", StringComparison.Ordinal) + 6;
        int pathEnd = spilled.IndexOf('"', pathStart);
        string abs = spilled.Substring(pathStart, pathEnd - pathStart);
        Assert.True(File.Exists(abs));
        Assert.Equal(big, File.ReadAllText(abs));

        string small = "hello";
        Assert.Equal(
            small,
            AgentOrchestrator.SpillLargeToolOutput(small, _tempRoot, "w1", "s1", "call_small")
        );
    }

    [Fact(DisplayName = "P1-B6b: 事件队列有界 1000 + 背压保 TurnCompleted，Shutdown 排空")]
    public void T6_EventQueue_Bounded_Keeps_Critical_Drains_On_Shutdown()
    {
        try
        {
            for (int i = 0; i < 1200; i++)
                JsEventDispatcher.Publish(
                    "llm:stream-event",
                    new
                    {
                        sessionId = "s",
                        kind = "TextDelta",
                        n = i
                    }
                );
            Assert.True(
                JsEventDispatcher.PendingCountForTests <= 1000,
                $"队列应有界，实际 {JsEventDispatcher.PendingCountForTests}"
            );
            Assert.True(JsEventDispatcher.DroppedEvents > 0);

            JsEventDispatcher.Publish(
                "agent:turn-event",
                new { sessionId = "s", kind = "TurnCompleted" }
            );
            Assert.True(JsEventDispatcher.PendingCountForTests <= 1000);

            var drained = new List<string>();
            string? batch;
            while ((batch = JsEventDispatcher.DequeueBatchJson(128)) != null)
                drained.Add(batch);
            Assert.Contains(drained, b => b.Contains("TurnCompleted"));

            JsEventDispatcher.Publish(
                "agent:turn-event",
                new { sessionId = "s", kind = "TurnCompleted" }
            );
            JsEventDispatcher.Shutdown();
            Assert.Equal(0, JsEventDispatcher.PendingCountForTests);
        }
        finally
        {
            JsEventDispatcher.ClearForTests();
        }
    }

    // ---------- B7/T7：用量 ----------

    [Fact(DisplayName = "P1-B7a: Barrier 累加子会话全部轮次用量（非末条）")]
    public async Task T7_Barrier_Accumulates_All_Sub_Usage()
    {
        var coord = new SubAgentCoordinator(
            _service,
            _budgetService,
            new P1HungGateway(),
            _storageManager,
            null,
            null,
            _agentService,
            null
        );
        var main = await _service.CreateSessionAsync(_workspace.Id, "P1-B7 主会话");
        var sub = await _service.CreateSessionAsync(_workspace.Id, "P1-B7 子会话");
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.User("轮1")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            AssistantWithUsage("答1", 100, 10, 5)
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.User("轮2")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            AssistantWithUsage("答2", 200, 20, 10)
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            UnifiedChatMessage.User("轮3")
        );
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sub.Id,
            AssistantWithUsage("答3", 300, 30, 15)
        );

        var handle = new SubHandle
        {
            SubSessionId = sub.Id,
            CallId = "call-sub-1",
            Label = "子1",
            Task = Task.FromResult(AgentTurnResult.Completed(3, 0)),
            Cts = new CancellationTokenSource(),
            StartedAtUtc = DateTime.UtcNow,
        };
        var results = await coord.BarrierAsync(
            _workspace.Id,
            main.Id,
            new[] { handle },
            Array.Empty<(string, string)>(),
            CancellationToken.None,
            TimeSpan.FromSeconds(30)
        );
        Assert.Single(results);
        Assert.Equal(600, results[0].InputTokens);
        Assert.Equal(60, results[0].OutputTokens);
        Assert.Equal(30, results[0].CachedTokens);
    }

    [Fact(DisplayName = "P1-B7b: TurnCompleted 回填真实子耗（非 0）")]
    public async Task T7_TurnCompleted_Reports_Real_Sub_Usage()
    {
        int calls = 0;
        var gateway = new P1ScriptedGateway(
            null,
            _ =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n == 1)
                    return new List<UnifiedStreamEvent>
                    {
                        UnifiedStreamEvent.ToolDone(
                            "call_spawn_1",
                            "spawn_subagent",
                            "{\"task\":\"总结项目结构\"}"
                        ),
                        UnifiedStreamEvent.Completed(),
                    };
                return new List<UnifiedStreamEvent>
                {
                    UnifiedStreamEvent.Text("主任务完成。"),
                    UnifiedStreamEvent.Tokens(10, 5, 0, 15),
                    UnifiedStreamEvent.Completed(),
                };
            }
        );
        // 子轮同样走该网关：返回带实测用量的直答。
        var subGateway = new P1ScriptedGateway(
            null,
            _ => new List<UnifiedStreamEvent>
            {
                UnifiedStreamEvent.Text("子任务完成。"),
                UnifiedStreamEvent.Tokens(100, 20, 80, 200),
                UnifiedStreamEvent.Completed(),
            }
        );
        var session = await _service.CreateSessionAsync(_workspace.Id, "P1-B7b 会话");
        var mainOrch = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(),
            _storageManager,
            agentConfigService: _agentService,
            coordinator: new SubAgentCoordinator(
                _service,
                _budgetService,
                subGateway,
                _storageManager,
                null,
                null,
                _agentService,
                null
            )
        );

        AgentTurnResult result = await mainOrch.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "请用子代理总结后回答"
        );
        Assert.True(result.Success);

        var batches = new List<string>();
        string? batch;
        while ((batch = JsEventDispatcher.DequeueBatchJson(128)) != null)
            batches.Add(batch);
        string completed = batches.FirstOrDefault(b => b.Contains("TurnCompleted")) ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(completed));
        Assert.Contains("\"count\":1", completed);
        Assert.Contains("\"billedPrompt\":100", completed);
        Assert.Contains("\"billedCompletion\":20", completed);
    }

    // ---------- helpers ----------

    private static UnifiedChatMessage AssistantWithUsage(
        string text,
        int input,
        int output,
        int cached
    ) =>
        new()
        {
            Role = "assistant",
            Content = text,
            InputTokens = input,
            OutputTokens = output,
            CachedTokens = cached
        };

    private UnifiedLlmRequest NewLlmRequest() =>
        new()
        {
            RequestId = $"req_p1_{Guid.NewGuid():N}",
            Provider = new ProviderConfig
            {
                Id = "p1",
                Name = "P1",
                BaseUrl = "https://api.test/v1"
            },
            Model = new ModelConfig { Id = "m1", Protocol = LlmProtocolType.Chat },
            Messages = new List<UnifiedChatMessage>
            {
                new() { Role = "user", Content = "hi" }
            },
            HeaderContext = HeaderVariableResolver.CreateContext(
                sessionId: "sess_p1",
                modelId: "m1",
                providerName: "P1",
                providerId: "p1"
            ),
        };

    private sealed class P1ScriptedGateway : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>? _script;
        private readonly Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>? _fallback;

        public P1ScriptedGateway(
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
                    UnifiedStreamEvent.Text("p1 默认回复。"),
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

    private sealed class P1HungGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("不应到达");
            yield return UnifiedStreamEvent.Completed();
        }
    }

    private sealed class P1GateGateway : ILlmGatewayClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref Calls);
            var winner = await Task.WhenAny(_release.Task, Task.Delay(Timeout.Infinite, ct))
                .ConfigureAwait(false);
            _ = winner;
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("gate 放行，本轮完成。");
            yield return UnifiedStreamEvent.Completed();
        }
    }

    private sealed class P1CountingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public int CallCount;

        public P1CountingHandler(
            IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses
        )
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
