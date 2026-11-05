using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

public sealed class ToolBatchExecutionTests
{
    private sealed class DelayedTool : ITool
    {
        public string Name => "delay_probe";
        public string Description => "延迟桩";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"ms\":{\"type\":\"integer\"}},\"required\":[]}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public int Calls;
        public string? LastArgs;

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastArgs = args.GetRawText();
            var ms = 0L;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ms", out var el) && el.ValueKind == JsonValueKind.Number)
                ms = el.GetInt64();
            if (ms > 0) await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
            return ToolResult.Success("delay-ok:" + ms);
        }
    }

    private sealed class SeqTool : ITool
    {
        public string Name => "seq_probe";
        public string Description => "串行桩";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("seq-ok"));
    }

    [Fact]
    public async Task Two_slow_tools_run_concurrently()
    {
        using var f = new AgentFixture(extraTools: new ITool[] { new DelayedTool() });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "delay_probe", "{\"ms\":300}");
            r.AddCall("c2", "delay_probe", "{\"ms\":300}");
        });
        f.Llm.EnqueueContent("都跑完了。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await f.Session.RunTurnAsync("跑两个", CancellationToken.None);
        sw.Stop();
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        // 并行：总耗时远小于串行 600ms
        Assert.True(sw.ElapsedMilliseconds < 550, $"elapsed={sw.ElapsedMilliseconds}");
        f.AssertInvariants();
    }

    [Fact]
    public async Task Sequential_tool_degrades_whole_batch()
    {
        var delay = new DelayedTool();
        using var f = new AgentFixture(extraTools: new ITool[] { delay, new SeqTool() });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "seq_probe", "{}");
            r.AddCall("c2", "delay_probe", "{\"ms\":300}");
        });
        f.Llm.EnqueueContent("跑完了。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        sw.Stop();
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        // 串行降级：至少接近延迟本身（不严格断耗时，只断都执行了）
        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, toolMsgs.Count);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Tool_messages_follow_source_order()
    {
        using var f = new AgentFixture(extraTools: new ITool[] { new DelayedTool() });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "delay_probe", "{\"ms\":300}");
            r.AddCall("c2", "delay_probe", "{\"ms\":0}");
        });
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolIds = f.MessagesForTest().Where(m => m.Role == Role.Tool).Select(m => m.ToolCallId).ToList();
        Assert.Equal(new[] { "c1", "c2" }, toolIds.ToArray());
        f.AssertInvariants();
    }

    [Fact]
    public async Task Immediate_failure_does_not_block_others()
    {
        var delay = new DelayedTool();
        using var f = new AgentFixture(extraTools: new ITool[] { delay });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "delay_probe", "{\"ms\":\"not-a-number\"}");
            r.AddCall("c2", "delay_probe", "{\"ms\":10}");
        });
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        // c1 参数错（ms 应为 integer，字符串不可 coerce 报校验失败），c2 正常执行
        Assert.Equal(1, delay.Calls);
        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, toolMsgs.Count);
        Assert.Contains("参数校验失败", toolMsgs[0].Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Truncated_reply_with_tool_calls_executes_nothing()
    {
        var delay = new DelayedTool();
        using var f = new AgentFixture(extraTools: new ITool[] { delay });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "delay_probe", "{\"ms\":10}");
            r.TruncatedByLength = true;
        });
        f.Llm.EnqueueContent("重发。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        Assert.Equal(0, delay.Calls);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("未执行:", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Repeat_brake_still_counts()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 3; i++)
            f.Llm.Enqueue(r => r.AddCall("c" + i, "list_directory", "{\"path\":\".\"}"));
        f.Llm.EnqueueContent("列完了。");
        await f.Session.RunTurnAsync("列", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.True(toolMsgs.Count >= 3);
        Assert.Contains("同一个调用", toolMsgs[2].Content ?? string.Empty);
        f.AssertInvariants();
    }
}
