using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

public sealed class ToolDispatchValidationTests
{
    private sealed class CountingTool : ITool
    {
        public int Calls;
        public string Name => "count_probe";
        public string Description => "计数桩";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    private sealed class BoomTool : ITool
    {
        public string Name => "boom_probe";
        public string Description => "抛异常桩";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("kaboom");
    }

    [Fact]
    public async Task Wrong_param_type_produces_validation_message()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "edit", "{\"path\":42,\"old_string\":\"a\",\"new_string\":\"b\"}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("改文件", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("参数校验失败:", toolMsg.Content ?? string.Empty);
        Assert.Contains("期望 string，实际 number", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Validation_failure_does_not_execute_tool()
    {
        var probe = new CountingTool();
        using var f = new AgentFixture(extraTools: new ITool[] { probe });
        f.Llm.Enqueue(r => r.AddCall("c1", "count_probe", "{\"path\":42}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        Assert.Equal(0, probe.Calls);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Validation_failure_emits_started_and_finished()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "edit", "{\"path\":42,\"old_string\":\"a\",\"new_string\":\"b\"}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("改", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var trace = f.EventTrace();
        Assert.Equal(1, trace.Count(k => k == EventKinds.ToolStarted));
        Assert.Equal(1, trace.Count(k => k == EventKinds.ToolFinished));
        f.AssertInvariants();
    }

    [Fact]
    public async Task Json_parse_failure_uses_dedicated_prefix()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "read_file", "[not-json{{{"));
        f.Llm.EnqueueContent("换个方式。");
        await f.Session.RunTurnAsync("读", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("参数解析失败:", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Unknown_tool_lists_available_tools()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "nope_tool", "{}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("未知工具:", toolMsg.Content ?? string.Empty);
        Assert.Contains("可用工具:", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Mcp_tool_name_still_reports_not_connected()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => r.AddCall("c1", "mcp__x__y", "{}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("MCP 工具", toolMsg.Content ?? string.Empty);
        Assert.DoesNotContain("可用工具:", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }

    [Fact]
    public async Task Execution_exception_prefix()
    {
        using var f = new AgentFixture(extraTools: new ITool[] { new BoomTool() });
        f.Llm.Enqueue(r => r.AddCall("c1", "boom_probe", "{}"));
        f.Llm.EnqueueContent("收到。");
        await f.Session.RunTurnAsync("跑", CancellationToken.None);
        await f.WaitForKindAsync(EventKinds.TurnFinished);
        var toolMsg = f.MessagesForTest().First(m => m.Role == Role.Tool);
        Assert.Contains("执行失败:", toolMsg.Content ?? string.Empty);
        f.AssertInvariants();
    }
}
