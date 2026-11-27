using System.Net.Http;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactionSummaryExecutorTests
{
    private readonly ITokenBudgetService _budget = new TokenBudgetService();

    [Fact]
    public async Task Execute_Single_Request_Records_Usage_And_Final_Tokens()
    {
        var plan = CreatePlan(700, 1);
        var gateway = new RecordingCompactGateway();
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var result = await executor.ExecuteAsync(plan, CreateContext(), null);

        Assert.Single(gateway.Requests);
        Assert.Equal(1, result.LlmCallCount);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.CachedTokens);
        Assert.Equal(20, result.FinalSummaryTokens);
    }

    [Fact]
    public async Task Execute_Map_Reduce_Consumes_All_Chunks_In_Order()
    {
        var messages = new[]
        {
            UnifiedChatMessage.User("first-marker" + new string('前', 3200)),
            UnifiedChatMessage.User("second-marker" + new string('后', 3200)),
        };
        var model = new ModelConfig { MaxContextTokens = 3000, MaxOutputTokens = 256 };
        var plan = new CompactionSummaryPlanner(_budget).Plan(messages, "previous-marker", model);
        var gateway = new RecordingCompactGateway(
            (index, _, _) =>
                RecordingCompactGateway.Stream(
                    index < 2 ? $"map-summary-{index}" : "final-summary",
                    10,
                    2,
                    1
                )
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var result = await executor.ExecuteAsync(plan, CreateContext(), "previous-marker");

        Assert.Equal(3, gateway.Requests.Count);
        Assert.Equal(3, result.LlmCallCount);
        Assert.Equal(30, result.InputTokens);
        Assert.Equal(6, result.OutputTokens);
        Assert.Equal(3, result.CachedTokens);
        Assert.Equal(2, result.FinalSummaryTokens);
        Assert.Contains("first-marker", gateway.Requests[0].Messages[0].Content!);
        Assert.Contains("second-marker", gateway.Requests[1].Messages[0].Content!);
        string reduceBody = gateway.Requests[2].Messages[0].Content!;
        Assert.Contains("map-summary-0", reduceBody);
        Assert.Contains("map-summary-1", reduceBody);
        Assert.Contains("previous-marker", reduceBody);
        Assert.Equal(
            gateway.Requests.Count,
            gateway.Requests.Select(request => request.RequestId).Distinct().Count()
        );
    }

    [Fact]
    public async Task Execute_Map_Failure_Does_Not_Return_Success()
    {
        var plan = CreatePlan(700, 2);
        var gateway = new RecordingCompactGateway(
            (index, _, _) =>
                index == 1
                    ? Throw("map-failure")
                    : RecordingCompactGateway.Stream("map-summary", 10, 2, 1)
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var exception = await Assert.ThrowsAsync<CompactionSummaryException>(
            () => executor.ExecuteAsync(plan, CreateContext(), null)
        );

        Assert.Equal("COMPACT_LLM_ERROR", exception.Code);
        Assert.Equal(2, gateway.Requests.Count);
    }

    [Fact]
    public async Task Execute_Cancellation_Stops_Later_Requests()
    {
        var plan = CreatePlan(700, 2);
        var gateway = new RecordingCompactGateway((_, _, _) => Cancel());
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var exception = await Assert.ThrowsAsync<CompactionSummaryException>(
            () => executor.ExecuteAsync(plan, CreateContext(), null)
        );

        Assert.Equal("COMPACT_SUMMARY_CANCELLED", exception.Code);
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task Execute_Recursively_Reduces_When_Combined_Summaries_Exceed_Budget()
    {
        var plan = CreatePlan(700, 3);
        var gateway = new RecordingCompactGateway(
            (index, _, _) =>
            {
                if (index < 3)
                    return RecordingCompactGateway.Stream(new string('a', 1050), 10, 300, 0);
                if (index == 3)
                    return RecordingCompactGateway.Stream(new string('b', 350), 10, 100, 0);
                return RecordingCompactGateway.Stream("final-recursive-summary", 10, 20, 0);
            }
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var result = await executor.ExecuteAsync(plan, CreateContext(), null);

        Assert.Equal("final-recursive-summary", result.Summary);
        Assert.Equal(5, result.LlmCallCount);
        Assert.Equal(5, gateway.Requests.Count);
    }

    [Fact]
    public async Task Execute_Throws_When_Reduce_Depth_Limit_Is_Reached()
    {
        var plan = CreatePlan(500, 3);
        var gateway = new RecordingCompactGateway(
            (index, _, _) => RecordingCompactGateway.Stream(new string('a', 1050), 10, 300, 0)
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var exception = await Assert.ThrowsAsync<CompactionSummaryException>(
            () => executor.ExecuteAsync(plan, CreateContext(), null)
        );

        Assert.Equal("COMPACT_SUMMARY_TOO_LARGE", exception.Code);
        Assert.Equal(3, gateway.Requests.Count);
    }

    [Fact]
    public async Task Execute_Empty_Map_Summary_Stops_Before_Reduce()
    {
        var plan = CreatePlan(700, 2);
        var gateway = new RecordingCompactGateway(
            (index, _, _) =>
                RecordingCompactGateway.Stream(index == 0 ? string.Empty : "must-not-run", 10, 0, 0)
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var exception = await Assert.ThrowsAsync<CompactionSummaryException>(
            () => executor.ExecuteAsync(plan, CreateContext(), null)
        );

        Assert.Equal("COMPACT_SUMMARY_EMPTY", exception.Code);
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task Execute_Empty_Summary_Is_Explicit_Failure()
    {
        var plan = CreatePlan(700, 1);
        var gateway = new RecordingCompactGateway(
            (_, _, _) => RecordingCompactGateway.Stream(string.Empty, 10, 0, 0)
        );
        var executor = new CompactionSummaryExecutor(gateway, _budget);

        var exception = await Assert.ThrowsAsync<CompactionSummaryException>(
            () => executor.ExecuteAsync(plan, CreateContext(), null)
        );

        Assert.Equal("COMPACT_SUMMARY_EMPTY", exception.Code);
    }

    private CompactionSummaryPlan CreatePlan(int chunkLimit, int chunkCount)
    {
        var chunks = Enumerable
            .Range(1, chunkCount)
            .Select(index => new CompactionSummaryChunk(
                index,
                chunkCount,
                new[]
                {
                    new CompactionSummaryUnit(
                        1,
                        $"chunk-marker-{index}",
                        20,
                        new[] { $"message-{index}" },
                        false
                    ),
                },
                20
            ))
            .ToList();
        return new CompactionSummaryPlan(
            chunks,
            chunkLimit,
            chunkLimit,
            chunks.Sum(chunk => chunk.EstimatedTokens),
            chunkCount > 1,
            new CompactionSummaryCoverage(
                chunkCount,
                chunkCount,
                0,
                0,
                chunks
                    .SelectMany(chunk => chunk.Units)
                    .SelectMany(unit => unit.SourceMessageIds)
                    .ToList()
            )
        );
    }

    private static CompactionSummaryExecutionContext CreateContext() =>
        new(
            new ProviderConfig { Id = "provider-compact", Name = "Compact Provider" },
            new ModelConfig
            {
                Id = "model-compact",
                MaxContextTokens = 3000,
                MaxOutputTokens = 256
            },
            new HeaderResolutionContext(
                "workspace-compact",
                "Compact Workspace",
                "workspace-path",
                "session-compact",
                "session-hash",
                "provider-compact",
                "Compact Provider",
                "model-compact",
                Guid.NewGuid().ToString("N")
            ),
            "workspace-compact",
            "session-compact"
        );

    private static async IAsyncEnumerable<UnifiedStreamEvent> Throw(string message)
    {
        await Task.Yield();
        throw new HttpRequestException(message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<UnifiedStreamEvent> Cancel()
    {
        await Task.Yield();
        yield return UnifiedStreamEvent.Cancelled();
    }
}
