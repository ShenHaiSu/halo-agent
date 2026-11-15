#nullable enable
using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>P007：压缩后预算暂存基线（exact → compact-estimate → exact）验收。</summary>
public sealed class CompactBudgetBaselineTests
{
    private readonly TokenBudgetService _svc = new();

    private static ModelConfig BudgetModel() =>
        new()
        {
            Id = "m",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096
        };

    private static UnifiedChatMessage StubMessage(string id, string summary) =>
        new()
        {
            Id = id,
            Role = "user",
            Content = summary,
            Kind = MessageKind.CompactStub
        };

    private static CompactStubInfo StubInfo(string id, int summaryTokens) =>
        new()
        {
            MessageId = id,
            TurnId = "cturn-" + id,
            Summary = "摘要正文",
            SummaryTokens = summaryTokens,
            CreatedAtUtc = DateTime.UtcNow,
        };

    private static void AssertDtoEquals(ContextBudgetStatusDto a, ContextBudgetStatusDto b)
    {
        Assert.Equal(a.TotalContextTokens, b.TotalContextTokens);
        Assert.Equal(a.MaxOutputTokens, b.MaxOutputTokens);
        Assert.Equal(a.SafetyBuffer, b.SafetyBuffer);
        Assert.Equal(a.UsedInputTokens, b.UsedInputTokens);
        Assert.Equal(a.AvailableInputTokens, b.AvailableInputTokens);
        Assert.Equal(a.UsagePercentage, b.UsagePercentage);
        Assert.Equal(a.IsNearLimit, b.IsNearLimit);
        Assert.Equal(a.SessionHash, b.SessionHash);
        Assert.Equal(a.Protocol, b.Protocol);
        Assert.Equal(a.LastPromptTokens, b.LastPromptTokens);
        Assert.Equal(a.LastCachedTokens, b.LastCachedTokens);
        Assert.Equal(a.CacheHitRate, b.CacheHitRate);
        Assert.Equal(a.UsedInputIsEstimated, b.UsedInputIsEstimated);
        Assert.Equal(a.OccupancyState, b.OccupancyState);
        Assert.Equal(a.OccupancyBaseline, b.OccupancyBaseline);
    }

    [Fact(DisplayName = "P007 baseline: compact-estimate right after compact")]
    public void Immediately_After_Compact_Is_CompactEstimate()
    {
        var stub = StubInfo("msg_stub_1", 800);
        var active = new List<UnifiedChatMessage>
        {
            StubMessage("msg_stub_1", "摘要正文"),
            UnifiedChatMessage.User("后续问题一"),
            UnifiedChatMessage.Assistant("后续答复一"),
        };

        var dto = _svc.CalculateBudgetStatus(BudgetModel(), active, null, 0, stub);

        Assert.Equal("compact-estimate", dto.OccupancyBaseline);
        Assert.Equal("known", dto.OccupancyState);
        Assert.Null(dto.LastPromptTokens);
        Assert.True(dto.UsedInputIsEstimated);
        Assert.True(dto.UsedInputTokens >= TokenBudgetService.CompactBaselineMinTokens);
    }

    [Fact(DisplayName = "P007 baseline A: summary plus tail estimate")]
    public void BaselineA_Equals_Max_Floor_Summary_Plus_Tail()
    {
        var stub = StubInfo("msg_stub_a", 2000);
        var tail1 = UnifiedChatMessage.User(new string('a', 350));
        var tail2 = UnifiedChatMessage.Assistant(new string('b', 700));
        var active = new List<UnifiedChatMessage>
        {
            StubMessage("msg_stub_a", "摘要正文"),
            tail1,
            tail2
        };

        var dto = _svc.CalculateBudgetStatus(BudgetModel(), active, null, 0, stub);

        int tail =
            TokenBudgetService.EstimateMessageTokens(tail1)
            + TokenBudgetService.EstimateMessageTokens(tail2);
        int expected = Math.Max(TokenBudgetService.CompactBaselineMinTokens, 2000 + tail);
        Assert.Equal("compact-estimate", dto.OccupancyBaseline);
        Assert.Equal(expected, dto.UsedInputTokens);
        Assert.InRange(dto.UsedInputTokens, expected * 0.9, expected * 1.1);
    }

    [Fact(DisplayName = "P007 baseline B: floor only under short summary")]
    public void Baseline_Floor_Only_When_Short_Summary()
    {
        // P007：短摘要无尾部时两种口径同值（下限口径）；含尾部时按当前口径开关断言。
        var stub = StubInfo("msg_stub_b", 300);
        var active = new List<UnifiedChatMessage> { StubMessage("msg_stub_b", "摘要正文") };
        var dto = _svc.CalculateBudgetStatus(BudgetModel(), active, null, 0, stub);
        Assert.Equal("compact-estimate", dto.OccupancyBaseline);
        Assert.Equal(TokenBudgetService.CompactBaselineMinTokens, dto.UsedInputTokens);

        var stub2 = StubInfo("msg_stub_b2", 2000);
        var tail = UnifiedChatMessage.User(new string('c', 700));
        var active2 = new List<UnifiedChatMessage> { StubMessage("msg_stub_b2", "摘要正文"), tail };
        var dto2 = _svc.CalculateBudgetStatus(BudgetModel(), active2, null, 0, stub2);
        int tailTokens = TokenBudgetService.EstimateMessageTokens(tail);
        int expected = TokenBudgetService.CompactBaselineIncludesTail
            ? Math.Max(TokenBudgetService.CompactBaselineMinTokens, 2000 + tailTokens)
            : Math.Max(TokenBudgetService.CompactBaselineMinTokens, 2000);
        Assert.Equal(expected, dto2.UsedInputTokens);
    }

    [Fact(DisplayName = "P007 calibration: next usage back to exact")]
    public void Next_Usage_To_Exact()
    {
        var stub = StubInfo("msg_stub_c", 800);
        var active = new List<UnifiedChatMessage>
        {
            StubMessage("msg_stub_c", "摘要正文"),
            UnifiedChatMessage.User("继续追问"),
        };
        var calibrated = UnifiedChatMessage.Assistant("新答复");
        calibrated.InputTokens = 6000;
        calibrated.OutputTokens = 100;
        active.Add(calibrated);

        var dto = _svc.CalculateBudgetStatus(BudgetModel(), active, null, 0, stub);

        Assert.Equal("exact", dto.OccupancyBaseline);
        Assert.Equal("known", dto.OccupancyState);
        Assert.Equal(6000, dto.UsedInputTokens);
        Assert.Equal(6000, dto.LastPromptTokens);
        Assert.False(dto.UsedInputIsEstimated);
    }

    [Fact(DisplayName = "P007 regression: null stub equals legacy fields")]
    public void Null_Stub_Matches_Legacy()
    {
        var withSnapshot = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hello"),
            AssistantWithSnapshot(48083),
        };
        var def = _svc.CalculateBudgetStatus(BudgetModel(), withSnapshot);
        var explicitNull = _svc.CalculateBudgetStatus(BudgetModel(), withSnapshot, null, 0, null);
        AssertDtoEquals(def, explicitNull);
        Assert.Equal("exact", explicitNull.OccupancyBaseline);
        Assert.Equal("known", explicitNull.OccupancyState);
        Assert.False(explicitNull.UsedInputIsEstimated);

        var noSnapshot = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hello"),
            UnifiedChatMessage.Assistant("hi"),
        };
        var def2 = _svc.CalculateBudgetStatus(BudgetModel(), noSnapshot);
        var explicitNull2 = _svc.CalculateBudgetStatus(BudgetModel(), noSnapshot, null, 0, null);
        AssertDtoEquals(def2, explicitNull2);
        Assert.Equal("estimated", explicitNull2.OccupancyBaseline);
        Assert.Equal("unknown", explicitNull2.OccupancyState);
        Assert.True(explicitNull2.UsedInputIsEstimated);
    }

    [Fact(DisplayName = "P007 stub only: baseline is floor of summary")]
    public void StubOnly_NoTail_Uses_Floor()
    {
        var stub = StubInfo("msg_stub_d", 812);
        var active = new List<UnifiedChatMessage> { StubMessage("msg_stub_d", "摘要正文") };

        var dto = _svc.CalculateBudgetStatus(BudgetModel(), active, null, 0, stub);

        Assert.Equal("compact-estimate", dto.OccupancyBaseline);
        Assert.Equal(
            Math.Max(TokenBudgetService.CompactBaselineMinTokens, 812),
            dto.UsedInputTokens
        );
        Assert.True(dto.UsagePercentage >= 0);
        double expectedPct = Math.Min(
            100.0,
            Math.Round((double)dto.UsedInputTokens / dto.TotalContextTokens * 100.0, 1)
        );
        Assert.Equal(expectedPct, dto.UsagePercentage);

        var empty = _svc.CalculateBudgetStatus(
            BudgetModel(),
            new List<UnifiedChatMessage>(),
            null,
            0,
            stub
        );
        Assert.Equal("exact", empty.OccupancyBaseline);
        Assert.Equal(0, empty.UsedInputTokens);
    }

    private static UnifiedChatMessage AssistantWithSnapshot(int input)
    {
        var m = UnifiedChatMessage.Assistant("reply");
        m.InputTokens = input;
        m.OutputTokens = 100;
        return m;
    }
}
