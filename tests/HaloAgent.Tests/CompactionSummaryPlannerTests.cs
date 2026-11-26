using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactionSummaryPlannerTests
{
    private readonly ITokenBudgetService _budget = new TokenBudgetService();

    [Fact]
    public void Plan_Includes_All_Twenty_Groups()
    {
        var messages = Enumerable
            .Range(0, 20)
            .Select(index => UnifiedChatMessage.User($"history-marker-{index:00}"))
            .ToList();

        var plan = new CompactionSummaryPlanner(_budget).Plan(messages, null, DefaultModel);

        Assert.Equal(20, plan.Coverage.CoveredMessageCount);
        Assert.Equal(20, plan.Coverage.SummarizedMessageCount);
        Assert.Equal(0, plan.Coverage.ExcludedMessageCount);
        for (int i = 0; i < 20; i++)
            Assert.Contains(
                plan.Chunks.SelectMany(chunk => chunk.Units),
                unit => unit.Text.Contains($"history-marker-{i:00}")
            );
    }

    [Fact]
    public void Plan_Keeps_Tool_Call_And_Result_In_Same_Chunk()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User(new string('前', 3200)),
            UnifiedChatMessage.Assistant(
                "scanning",
                new List<UnifiedToolCall>
                {
                    new("call-compact-a", null, "read_file", "{\"path\":\"a\"}")
                }
            ),
            UnifiedChatMessage.Tool("call-compact-a", "tool-result-marker", "read_file"),
            UnifiedChatMessage.User("after-tool-marker"),
        };

        var plan = new CompactionSummaryPlanner(_budget).Plan(messages, null, SmallModel);

        var toolUnit = Assert.Single(
            plan.Chunks.SelectMany(chunk => chunk.Units)
                .Where(unit => unit.SourceMessageIds.Contains(messages[2].Id))
        );
        Assert.Contains(messages[1].Id, toolUnit.SourceMessageIds);
        Assert.Contains(messages[2].Id, toolUnit.SourceMessageIds);
        Assert.Contains("tool-result-marker", toolUnit.Text);
    }

    [Fact]
    public void Plan_Puts_Previous_Summary_First_And_Omits_Leading_System()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.System("system-prompt-must-not-enter-body"),
            UnifiedChatMessage.User("new-history-marker"),
        };

        var plan = new CompactionSummaryPlanner(_budget).Plan(
            messages,
            "previous-summary-marker",
            DefaultModel
        );

        Assert.Contains("previous-summary-marker", plan.Chunks[0].Units[0].Text);
        Assert.DoesNotContain(
            plan.Chunks.SelectMany(chunk => chunk.Units),
            unit => unit.Text.Contains("system-prompt-must-not-enter-body")
        );
        Assert.Equal(1, plan.Coverage.CoveredMessageCount);
    }

    [Fact]
    public void Plan_Counts_Filtered_Messages_As_Excluded()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("kept-marker"),
            UnifiedChatMessage.Assistant("[error] failed-status"),
            UnifiedChatMessage.Assistant(string.Empty),
        };

        var plan = new CompactionSummaryPlanner(_budget).Plan(messages, null, DefaultModel);

        Assert.Equal(3, plan.Coverage.CoveredMessageCount);
        Assert.Equal(1, plan.Coverage.SummarizedMessageCount);
        Assert.Equal(2, plan.Coverage.ExcludedMessageCount);
        Assert.Equal(
            plan.Coverage.CoveredMessageCount,
            plan.Coverage.SummarizedMessageCount + plan.Coverage.ExcludedMessageCount
        );
    }

    [Fact]
    public void Plan_Records_Single_Message_Truncation()
    {
        var plan = new CompactionSummaryPlanner(_budget).Plan(
            new[] { UnifiedChatMessage.User(new string('文', 2600)) },
            null,
            DefaultModel
        );

        Assert.Equal(1, plan.Coverage.TruncatedMessageCount);
        Assert.Contains(plan.Chunks.SelectMany(chunk => chunk.Units), unit => unit.Truncated);
    }

    [Fact]
    public void Plan_Throws_When_Atomic_Unit_Cannot_Fit()
    {
        var exception = Assert.Throws<CompactionSummaryException>(
            () =>
                new CompactionSummaryPlanner(_budget).Plan(
                    new[] { UnifiedChatMessage.User("necessary-message") },
                    null,
                    new ModelConfig { MaxContextTokens = 64, MaxOutputTokens = 8 }
                )
        );

        Assert.Equal("COMPACT_SUMMARY_UNIT_TOO_LARGE", exception.Code);
    }

    [Fact]
    public void Plan_Throws_Instead_Of_Dropping_Chunks_Beyond_Limit()
    {
        var messages = Enumerable
            .Range(0, 20)
            .Select(_ => UnifiedChatMessage.User(new string('文', 120)))
            .ToList();
        var model = new ModelConfig { MaxContextTokens = 3000, MaxOutputTokens = 256 };

        var exception = Assert.Throws<CompactionSummaryException>(
            () => new CompactionSummaryPlanner(_budget).Plan(messages, null, model)
        );

        Assert.Equal("COMPACT_SUMMARY_TOO_LARGE", exception.Code);
    }

    [Fact]
    public void Plan_Applies_Prompt_Reserve_And_Safety_Margin()
    {
        var plan = new CompactionSummaryPlanner(_budget).Plan(
            new[] { UnifiedChatMessage.User("budget-marker") },
            null,
            DefaultModel
        );

        Assert.Equal((int)((plan.InputTokenLimit - 512) * 0.9), plan.ChunkTokenLimit);
        Assert.All(plan.Chunks, chunk => Assert.Equal(plan.Chunks.Count, chunk.Total));
    }

    private static ModelConfig DefaultModel =>
        new()
        {
            Id = "model-compact-default",
            MaxContextTokens = 128000,
            MaxOutputTokens = 4096,
        };

    private static ModelConfig SmallModel =>
        new()
        {
            Id = "model-compact-small",
            MaxContextTokens = 3000,
            MaxOutputTokens = 256,
        };
}
