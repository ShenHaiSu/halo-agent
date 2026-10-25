#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-06/08 §1：ask_user 解析/校验/超时/并发。</summary>
public sealed class Stage09AskUserTests : IDisposable
{
    public Stage09AskUserTests()
    {
        AskUserGate.ClearAll();
    }

    public void Dispose()
    {
        AskUserGate.ClearAll();
    }

    private static ToolContext Ctx(string session) =>
        new()
        {
            WorkspaceRoot = string.Empty,
            WorkspaceId = "ws",
            SessionId = session,
            DataRoot = System.IO.Path.GetTempPath(),
        };

    [Fact(DisplayName = "S09-A1: YAML 糖与 JSON 等价")]
    public void Yaml_Equals_Json()
    {
        string json =
            "{\"questions\":[{\"id\":\"fw\",\"question\":\"用哪个框架？\",\"options\":[{\"label\":\"React\",\"description\":\"生态大\"},{\"label\":\"Vue\"}],\"allowCustom\":true},{\"id\":\"open\",\"question\":\"补充说明？\"}]}";
        var (jq, _) = AskUserTool.ParseArguments(json, 300);
        string yaml =
            @"
# 问卷注释
questions:
  - id: fw
    question: 用哪个框架？
    options:
      - label: React
        description: 生态大  # 行尾注释
      - label: Vue
    allowCustom: true
  - id: open
    question: 补充说明？
";
        var (yq, _) = AskUserTool.ParseArguments("{\"questions\":" + JsonEscape(yaml) + "}", 300);
        Assert.Equal(2, jq.Count);
        Assert.Equal(2, yq.Count);
        Assert.Equal(jq[0].Id, yq[0].Id);
        Assert.Equal(jq[0].Question, yq[0].Question);
        Assert.Equal(jq[0].Options.Count, yq[0].Options.Count);
        Assert.Equal("React", yq[0].Options[0].Label);
        Assert.Equal("生态大", yq[0].Options[0].Description);
        Assert.True(yq[0].AllowCustom);
        Assert.Equal(jq[1].Id, yq[1].Id);
        Assert.Null(AskUserTool.ValidateQuestions(yq));
    }

    [Fact(DisplayName = "S09-A2: question 单数别名 + 顶层数组兼容")]
    public void SingularAlias_And_TopArray_Compat()
    {
        var (one, _) = AskUserTool.ParseArguments(
            "{\"question\":{\"id\":\"q1\",\"question\":\"继续吗？\"}}",
            300
        );
        Assert.Single(one);
        Assert.Equal("q1", one[0].Id);
        var (arr, _) = AskUserTool.ParseArguments("[{\"id\":\"a\",\"question\":\"A？\"}]", 300);
        Assert.Single(arr);
        Assert.Equal("a", arr[0].Id);
    }

    [Fact(DisplayName = "S09-A3: 5 题拒绝且无残留 pending")]
    public async Task FiveQuestions_Rejected_NoPending()
    {
        string session = "sess_" + Guid.NewGuid().ToString("N")[..8];
        string args =
            "{\"questions\":["
            + "{\"id\":\"q1\",\"question\":\"1？\"},{\"id\":\"q2\",\"question\":\"2？\"},"
            + "{\"id\":\"q3\",\"question\":\"3？\"},{\"id\":\"q4\",\"question\":\"4？\"},"
            + "{\"id\":\"q5\",\"question\":\"5？\"}]}";
        var tool = new AskUserTool();
        var result = await tool.ExecuteAsync(new ToolCallDto("c1", "ask_user", args), Ctx(session));
        Assert.False(result.Success);
        Assert.Contains("最多提 4 道", result.Error);
        Assert.False(AskUserGate.HasPending(session));
    }

    [Fact(DisplayName = "S09-A4: 双 pending 第二个 Fail + ticket 错配")]
    public void DoublePending_SecondFails_TicketMismatch()
    {
        string session = "sess_" + Guid.NewGuid().ToString("N")[..8];
        var qs = new System.Collections.Generic.List<AskUserQuestion>
        {
            new() { Id = "q1", Question = "继续吗？" },
        };
        var (created1, ticket1, _) = AskUserGate.TryCreate(session, "call_1", qs);
        Assert.True(created1);
        var (created2, _, conflict) = AskUserGate.TryCreate(session, "call_2", qs);
        Assert.False(created2);
        Assert.Equal(ticket1, conflict);

        var (okWrong, code, _) = AskUserGate.TryResolve(session, "ask_wrongticket", "{}");
        Assert.False(okWrong);
        Assert.Equal("ASK_TICKET_MISMATCH", code);
        Assert.True(AskUserGate.HasPending(session));

        var (ok, _, _) = AskUserGate.TryResolve(
            session,
            ticket1,
            "{\"answers\":[{\"id\":\"q1\",\"selected\":[],\"customInput\":\"\",\"skipped\":true}]}"
        );
        Assert.True(ok);
        Assert.False(AskUserGate.HasPending(session));
    }

    [Fact(DisplayName = "S09-A5: 超时不挂起（1s 超时）")]
    public async Task Timeout_DoesNotHang()
    {
        string session = "sess_" + Guid.NewGuid().ToString("N")[..8];
        var tool = new AskUserTool();
        var sw = Stopwatch.StartNew();
        var result = await tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "ask_user",
                "{\"questions\":[{\"id\":\"q1\",\"question\":\"继续吗？\"}],\"timeoutSec\":1}"
            ),
            Ctx(session)
        );
        sw.Stop();
        Assert.False(result.Success);
        Assert.Contains("超时", result.Error);
        Assert.False(AskUserGate.HasPending(session));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"耗时 {sw.Elapsed}");
    }

    [Fact(DisplayName = "S09-A6: 提交答案后返回摘要 + json 块")]
    public async Task Resolve_ReturnsSummary_And_JsonBlock()
    {
        string session = "sess_" + Guid.NewGuid().ToString("N")[..8];
        var tool = new AskUserTool();
        var task = tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "ask_user",
                "{\"questions\":[{\"id\":\"fw\",\"question\":\"用哪个框架？\",\"options\":[{\"label\":\"React\"},{\"label\":\"Vue\"}]}]}"
            ),
            Ctx(session)
        );
        string? ticket = null;
        for (int i = 0; i < 250 && ticket == null; i++)
        {
            await Task.Delay(20);
            ticket = AskUserGate.TryGetTicket(session);
        }
        Assert.NotNull(ticket);
        var (ok, _, _) = AskUserGate.TryResolve(
            session,
            ticket!,
            "{\"answers\":[{\"id\":\"fw\",\"selected\":[\"React\"],\"customInput\":\"\",\"skipped\":false}]}"
        );
        Assert.True(ok);
        var result = await task;
        Assert.True(result.Success, result.Error);
        Assert.Contains("用户已回答（1/1）", result.Output);
        Assert.Contains("[fw]", result.Output);
        Assert.Contains("```json", result.Output);
        Assert.Contains("React", result.Output);
    }

    [Fact(DisplayName = "S09-A7: 取消转 Fail")]
    public async Task Cancel_TransToFail()
    {
        string session = "sess_" + Guid.NewGuid().ToString("N")[..8];
        var tool = new AskUserTool();
        using var cts = new CancellationTokenSource();
        var task = tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "ask_user",
                "{\"questions\":[{\"id\":\"q1\",\"question\":\"继续吗？\"}]}"
            ),
            Ctx(session),
            cts.Token
        );
        for (int i = 0; i < 250 && !AskUserGate.HasPending(session); i++)
            await Task.Delay(20);
        Assert.True(AskUserGate.HasPending(session));
        cts.Cancel();
        var result = await task;
        Assert.False(result.Success);
        Assert.Contains("取消", result.Error);
        Assert.False(AskUserGate.HasPending(session));
    }

    [Fact(DisplayName = "S09-A8: allowCustom 冻结为 true（历史 false 兼容忽略）")]
    public void AllowCustom_FrozenTrue()
    {
        var (jq, _) = AskUserTool.ParseArguments(
            "{\"questions\":[{\"id\":\"q1\",\"question\":\"选哪个？\",\"options\":[{\"label\":\"A\"}],\"allowCustom\":false}]}",
            300
        );
        Assert.Single(jq);
        Assert.True(jq[0].AllowCustom);
        Assert.Null(AskUserTool.ValidateQuestions(jq));
    }

    private static string JsonEscape(string s) =>
        "\""
        + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")
        + "\"";
}
