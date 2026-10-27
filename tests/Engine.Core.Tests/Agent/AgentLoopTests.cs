using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
 using HaloAgent.Engine.Core.Tools;
 using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>
/// agent 循环核心场景子集（S01/S02/S03/S04/S05/S07/S08/S09/S10/S11/S13/S15/S16/S17/S18 见 CompactTests/S19 见 CompactTests）。
/// 每个测试末尾都断言历史不变式；关键断言都基于 FakeLlmClient.Calls 的入参快照。
/// </summary>
public sealed class AgentLoopTests
{
    // ---------- 测试用慢工具：可配置时长，取消时抛 OperationCanceledException ----------

    private sealed class SlowTool : ITool
    {
        public string Name => "slow_probe";
        public string Description => "测试用慢工具";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);
        public bool WasCanceled { get; private set; }

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Duration, ct).ConfigureAwait(false);
                return ToolResult.Success("slow-ok");
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
        }
    }

    // ---------- 串行探测工具：记录最大并发度 ----------

    private sealed class ProbeTool : ITool
    {
        public string Name => "probe_sleep";
        public string Description => "测试用串行探测工具";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public int Current;
        public int MaxObserved;

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var c = Interlocked.Increment(ref Current);
            int prev, next;
            do
            {
                prev = MaxObserved;
                next = Math.Max(prev, c);
            }
            while (Interlocked.CompareExchange(ref MaxObserved, next, prev) != prev);

            try
            {
                await Task.Delay(150).ConfigureAwait(false);
                return ToolResult.Success("probe-ok");
            }
            finally
            {
                Interlocked.Decrement(ref Current);
            }
        }
    }

    // ---------- S01：纯文本回合 ----------

    [Fact]
    public async Task S01_plain_text_turn()
    {
        using var f = new AgentFixture();
        f.Llm.EnqueueContent("你好，我是 Halo。");

        var result = await f.Session.RunTurnAsync("hi", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("你好，我是 Halo。", result.Text);
        Assert.Single(f.Llm.Calls);

        var trace = f.EventTrace();
        Assert.Equal(EventKinds.TurnStarted, trace[0]);
        Assert.Contains(EventKinds.TextDelta, trace);
        Assert.Contains(EventKinds.TurnFinished, trace);
        Assert.True(
            Array.IndexOf(trace, EventKinds.TurnStarted) <
            Array.IndexOf(trace, EventKinds.TextDelta));
        Assert.True(
            Array.IndexOf(trace, EventKinds.TextDelta) <
            Array.IndexOf(trace, EventKinds.TurnFinished));

        // 流式 delta 拼起来恰好等于一次内容（无重复、无丢失）。
        Assert.Equal("你好，我是 Halo。", f.Sink.ConcatTextOf(EventKinds.TextDelta));

        f.AssertInvariants();
    }

    // ---------- S02：单工具调用 ----------

    [Fact]
    public async Task S02_single_tool_call()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "list_directory", "{\"path\":\".\"}"));
        f.Llm.EnqueueContent("目录里一共 0 个文件。");

        var result = await f.Session.RunTurnAsync("看看目录", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("目录里一共 0 个文件。", result.Text);
        Assert.Equal(2, f.Llm.Calls.Count);
        Assert.True(1 <= f.Llm.Calls[0].Tools!.Count);

        // 第二次模型调用必须能看到 tool 结果，且 toolCallId 全程一致。
        var second = f.Llm.Calls[1].Messages;
        var toolMsg = second.FirstOrDefault(m => m.Role == Role.Tool);
        Assert.NotNull(toolMsg);
        var asst = f.MessagesForTest().First(m => m.Role == Role.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal(asst.ToolCalls![0].Id, toolMsg!.ToolCallId);

        var trace = f.EventTrace();
        Assert.Contains(EventKinds.ToolStarted, trace);
        Assert.Contains(EventKinds.ToolFinished, trace);

        f.AssertInvariants();
    }

    // ---------- S03：多工具严格串行 ----------

    [Fact]
    public async Task S03_multiple_tools_run_sequentially()
    {
        var probe = new ProbeTool();
        using var f = new AgentFixture(extraTools: new ITool[] { probe });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "probe_sleep", "{\"n\":1}");
            r.AddCall("c2", "probe_sleep", "{\"n\":2}");
            r.AddCall("c3", "probe_sleep", "{\"n\":3}");
        });
        f.Llm.EnqueueContent("三个都跑完了。");

        var result = await f.Session.RunTurnAsync("跑三个", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("三个都跑完了。", result.Text);
        Assert.Equal(2, f.Llm.Calls.Count);
        // 任何时刻只有一个工具在执行。
        Assert.Equal(1, probe.MaxObserved);

        var trace = f.EventTrace();
        Assert.Equal(3, trace.Count(k => k == EventKinds.ToolStarted));
        Assert.Equal(3, trace.Count(k => k == EventKinds.ToolFinished));

        var msgs = f.MessagesForTest();
        var ids = msgs.Where(m => m.Role == Role.Assistant && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!).Select(t => t.Id).ToList();
        var toolIds = msgs.Where(m => m.Role == Role.Tool).Select(m => m.ToolCallId).ToList();
        Assert.Equal(new[] { "c1", "c2", "c3" }, ids.ToArray());
        Assert.Equal(new[] { "c1", "c2", "c3" }, toolIds.ToArray());

        f.AssertInvariants();
    }

    // ---------- S04：工具成功后模型继续 ----------

    [Fact]
    public async Task S04_tool_success_model_continues_with_tool_result()
    {
        using var f = new AgentFixture();
        await File.WriteAllTextAsync(Path.Combine(f.Cwd, "marker_s04.txt"), "marker-body-123");
        f.Llm.Enqueue(r => r.AddCall("c1", "read_file", "{\"path\":\"marker_s04.txt\"}"));
        f.Llm.EnqueueContent("文件内容是 marker-body-123。");

        var result = await f.Session.RunTurnAsync("读文件", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("文件内容是 marker-body-123。", result.Text);

        // 落库序列：user → assistant(+tool_calls) → tool → assistant，无缺漏。
        var msgs = f.MessagesForTest();
        Assert.Equal(
            new[] { "user", "assistant", "tool", "assistant" },
            msgs.Select(m => m.RoleWire).ToArray());

        // 第二次模型调用收到了工具输出。
        var seenByModel = f.Llm.Calls[1].Messages
            .Any(m => m.Role == Role.Tool && (m.Content ?? string.Empty).Contains("marker-body-123"));
        Assert.True(seenByModel);

        f.AssertInvariants();
    }

    // ---------- S05：工具失败后模型重试 ----------

    [Fact]
    public async Task S05_tool_failure_is_visible_to_model_retry()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "read_file", "{\"path\":\"nope-missing-s05.txt\"}"));
        f.Llm.EnqueueContent("收到，文件不存在，我换个方案。");

        var result = await f.Session.RunTurnAsync("读一个不存在的文件", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("收到，文件不存在，我换个方案。", result.Text);
        Assert.Equal(2, f.Llm.Calls.Count);

        var seenByModel = f.Llm.Calls[1].Messages
            .Any(m => m.Role == Role.Tool && (m.Content ?? string.Empty).Contains("不存在"));
        Assert.True(seenByModel);

        f.AssertInvariants();
    }

    // ---------- S07：工具执行中中断，剩余补占位 ----------

    [Fact]
    public async Task S07_cancel_during_tool_fills_placeholders()
    {
        var slow = new SlowTool { Duration = TimeSpan.FromSeconds(30) };
        using var f = new AgentFixture(extraTools: new ITool[] { slow });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "slow_probe", "{}");
            r.AddCall("c2", "list_directory", "{\"path\":\".\"}");
        });

        using var cts = new CancellationTokenSource(500);
        var result = await f.Session.RunTurnAsync("跑", cts.Token);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        // 会话内部把中断消化为截断收尾（不抛），两个 tool_call 都有占位响应。
        Assert.True(result.Truncated);
        Assert.Contains("中断", result.Text);
        Assert.True(slow.WasCanceled);

        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, toolMsgs.Count);
        Assert.All(toolMsgs, m => Assert.Contains("用户中断", m.Content ?? string.Empty));

        f.AssertInvariants();
    }

    // ---------- S08：排队消息在轮边界注入 ----------

    [Fact]
    public async Task S08_queued_message_inserted_at_round_boundary()
    {
        var slow = new SlowTool { Duration = TimeSpan.FromMilliseconds(800) };
        using var f = new AgentFixture(extraTools: new ITool[] { slow });
        f.Llm.Enqueue(r => r.AddCall("c1", "slow_probe", "{}"));
        f.Llm.EnqueueContent("第一件和顺带的事都做完了。");

        var task = f.Session.RunTurnAsync("第一件事", CancellationToken.None);
        await Task.Delay(150);
        var queued = f.Session.Enqueue("顺便做第二件事");
        Assert.Equal(1, queued);
        var result = await task;
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("第一件和顺带的事都做完了。", result.Text);

        var msgs = f.MessagesForTest().ToList();
        var idxTool = msgs.FindIndex(m => m.Role == Role.Tool);
        var idxQueue = msgs.FindIndex(m => m.Role == Role.User && (m.Content ?? string.Empty).Contains("顺便"));
        Assert.True(idxTool >= 0, "tool 消息缺失");
        Assert.True(idxQueue >= 0, "排队消息缺失");
        Assert.True(idxQueue > idxTool, "排队消息插到了 tool_calls 与 tool 之间！");

        // 第二次模型调用的快照里必须包含排队注入的用户消息。
        var seenByModel = f.Llm.Calls[1].Messages
            .Any(m => m.Role == Role.User && (m.Content ?? string.Empty).Contains("顺便"));
        Assert.True(seenByModel);
        Assert.Contains(EventKinds.UserInjected, f.EventTrace());

        f.AssertInvariants();
    }

    // ---------- S09：重复 3 次提醒 ----------

    [Fact]
    public async Task S09_repeat_three_times_warns()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "list_directory", "{\"path\":\".\"}");
            r.AddCall("c2", "list_directory", "{\"path\":\".\"}");
            r.AddCall("c3", "list_directory", "{\"path\":\".\"}");
        });
        f.Llm.EnqueueContent("收到提醒，我换思路。");

        var result = await f.Session.RunTurnAsync("列目录", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.False(result.Truncated);
        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(3, toolMsgs.Count);
        Assert.Contains("重复第 3 次", toolMsgs[2].Content ?? string.Empty);

        // 提醒文本模型收到了（第二次调用快照里有）。
        var seenByModel = f.Llm.Calls[1].Messages
            .Any(m => m.Role == Role.Tool && (m.Content ?? string.Empty).Contains("重复第 3 次"));
        Assert.True(seenByModel);

        f.AssertInvariants();
    }

    // ---------- S10：重复 6 次中止 ----------

    [Fact]
    public async Task S10_repeat_six_times_aborts()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r =>
        {
            for (int k = 1; k <= 6; k++)
                r.AddCall("c" + k, "list_directory", "{\"path\":\".\"}");
        });

        var result = await f.Session.RunTurnAsync("列目录", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.True(result.Truncated);
        Assert.Contains("打转", result.Text);
        // 中止发生在首轮内，不需要第二次模型调用。
        Assert.Single(f.Llm.Calls);
        Assert.Equal(6, f.MessagesForTest().Count(m => m.Role == Role.Tool));

        f.AssertInvariants();
    }

    // ---------- S11：轮次用尽（无头收尾） ----------

    [Fact]
    public async Task S11_round_budget_exhausted_headless_finishes()
    {
        using var f = new AgentFixture(interactive: false, maxTurns: 1);
        f.Llm.Enqueue(r => r.AddCall("c1", "list_directory", "{\"path\":\".\"}"));

        var result = await f.Session.RunTurnAsync("开工", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.True(result.Truncated);
        Assert.Contains("轮上限", result.Text);
        // 预算在第二轮模型调用之前耗尽，只调了一次模型。
        Assert.Single(f.Llm.Calls);

        f.AssertInvariants();
    }

    // ---------- S11b：轮次用尽（交互询问后继续） ----------

    [Fact]
    public async Task S11b_round_budget_interactive_ask_can_continue()
    {
        using var f = new AgentFixture(interactive: true, maxTurns: 1);
        f.Llm.Enqueue(r => r.AddCall("c1", "list_directory", "{\"path\":\".\"}"));
        f.Llm.EnqueueContent("继续后的文本");

        var task = f.Session.RunTurnAsync("开工", CancellationToken.None);

        string? askId = null;
        for (int i = 0; i < 200 && askId is null; i++)
        {
            await Task.Delay(50);
            var pending = f.Session.PendingAsk;
            if (pending is not null && pending.Kind == "rounds")
                askId = pending.AskId;
        }

        Assert.NotNull(askId);
        var (accepted, _) = f.Session.AnswerAsk(askId!, allow: true, always: false);
        Assert.True(accepted);

        var result = await task;
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("继续后的文本", result.Text);
        Assert.Equal(2, f.Llm.Calls.Count);

        f.AssertInvariants();
    }

    // ---------- S13：429 重试且不重复文本 ----------

    [Fact]
    public async Task S13_retry_on_429_does_not_duplicate_text()
    {
        using var f = new AgentFixture(retry: 3);
        f.Llm.EnqueueThrow(new EndpointStatusException(429, "rate limited"));
        f.Llm.EnqueueContent("成功了一次");

        var result = await f.Session.RunTurnAsync("hi", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("成功了一次", result.Text);
        Assert.Equal(2, f.Llm.Calls.Count);
        Assert.Contains(EventKinds.LlmRetrying, f.EventTrace());

        // 事件里 text.delta 拼起来必须恰好等于一次内容（重试前的半截输出不许进历史）。
        Assert.Equal("成功了一次", f.Sink.ConcatTextOf(EventKinds.TextDelta));

        f.AssertInvariants();
    }

    // ---------- S15：参数解析失败回模型 ----------

    [Fact]
    public async Task S15_bad_arguments_returned_to_model()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "read_file", "[not-json{{{"));
        f.Llm.EnqueueContent("我换个方式读。");

        var result = await f.Session.RunTurnAsync("读文件", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("我换个方式读。", result.Text);
        Assert.False(result.Truncated);
        Assert.Equal(2, f.Llm.Calls.Count);

        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("工具参数解析失败", toolMsg.Content ?? string.Empty);

        f.AssertInvariants();
    }

    // ---------- S16：参数空串归一化 ----------

    [Fact]
    public async Task S16_empty_arguments_normalized_to_object()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "list_directory", string.Empty));
        f.Llm.EnqueueContent("列出来了。");

        var result = await f.Session.RunTurnAsync("列目录", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("列出来了。", result.Text);

        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.DoesNotContain("参数解析失败", toolMsg.Content ?? string.Empty);
        Assert.Contains("目录", toolMsg.Content ?? string.Empty);

        f.AssertInvariants();
    }

    // ---------- S17：参数截断修补 ----------

    [Theory]
    [InlineData("{\"path\":\"a")]
    [InlineData("{\"path\":\"a.txt\"")]
     [InlineData("{\"path\":\"a}")]
    public async Task S17_truncated_args_are_repaired(string badArgs)
    {
        using var f = new AgentFixture();
        Directory.CreateDirectory(Path.Combine(f.Cwd, "a"));
        await File.WriteAllTextAsync(Path.Combine(f.Cwd, "a.txt"), "x");
        f.Llm.Enqueue(r => r.AddCall("c1", "list_directory", badArgs));
        f.Llm.EnqueueContent("ok");

        var result = await f.Session.RunTurnAsync("hi", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);

        Assert.Equal("ok", result.Text);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.DoesNotContain("参数解析失败", toolMsg.Content ?? string.Empty);

        f.AssertInvariants();
    }
}
