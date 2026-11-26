using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactBridgeContractTests
{
    [Fact]
    public async Task NoOp_Publishes_CompactNoOp_Without_Completed()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        JsEventDispatcher.ClearForTests();
        var facade = CreateFacade(fixture, CompactionResult.NoOp(CompactionNoOpReasons.TooShort));
        try
        {
            var response = await facade.CompactSession(workspaceId, sessionId);
            Assert.True(response.Ok);
            var events = await WaitForTerminal("CompactNoOp", sessionId);
            Assert.Contains(events, item => EventKind(item) == "CompactStarted");
            Assert.DoesNotContain(events, item => EventKind(item) == "CompactCompleted");
            var terminal = events.First(item => EventKind(item) == "CompactNoOp");
            var noOpPayload = Payload(terminal);
            Assert.True(
                noOpPayload.TryGetProperty("noOpReason", out var reason),
                noOpPayload.GetRawText()
            );
            Assert.Equal("TOO_SHORT", reason.GetString());
        }
        finally
        {
            JsEventDispatcher.ClearForTests();
        }
    }

    [Fact]
    public async Task Completed_Publishes_New_Optional_Fields_In_Camel_Case()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        JsEventDispatcher.ClearForTests();
        var result = CompactionResult.OkStub(
            5,
            6,
            "stub-message-a",
            "stub-turn-a",
            20,
            "preview",
            null,
            "COMPACT_METADATA_WARNING:IOException",
            2
        );
        var facade = CreateFacade(fixture, result);
        try
        {
            var response = await facade.CompactSession(workspaceId, sessionId);
            Assert.True(response.Ok);
            var events = await WaitForTerminal("CompactCompleted", sessionId);
            var payload = Payload(events.First(item => EventKind(item) == "CompactCompleted"));
            Assert.Equal("stub-message-a", payload.GetProperty("stubMessageId").GetString());
            Assert.Equal("stub-turn-a", payload.GetProperty("stubTurnId").GetString());
            Assert.Equal(20, payload.GetProperty("summaryTokens").GetInt32());
            Assert.Equal(2, payload.GetProperty("compactLlmCallCount").GetInt32());
            Assert.Equal(
                "COMPACT_METADATA_WARNING:IOException",
                payload.GetProperty("metadataWarning").GetString()
            );
            Assert.DoesNotContain(events, item => EventKind(item) == "CompactNoOp");
        }
        finally
        {
            JsEventDispatcher.ClearForTests();
        }
    }

    [Fact]
    public async Task Failed_Publishes_CompactFailed()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        JsEventDispatcher.ClearForTests();
        var facade = CreateFacade(
            fixture,
            CompactionResult.Fail("COMPACT_LLM_ERROR", "model-failure")
        );
        try
        {
            var response = await facade.CompactSession(workspaceId, sessionId);
            Assert.True(response.Ok);
            var events = await WaitForTerminal("CompactFailed", sessionId);
            var payload = Payload(events.First(item => EventKind(item) == "CompactFailed"));
            Assert.Equal("COMPACT_LLM_ERROR", payload.GetProperty("errorCode").GetString());
            Assert.Equal("model-failure", payload.GetProperty("errorMessage").GetString());
            Assert.DoesNotContain(events, item => EventKind(item) == "CompactCompleted");
        }
        finally
        {
            JsEventDispatcher.ClearForTests();
        }
    }

    private static HostBridgeFacade CreateFacade(
        CompactTestWorkspace fixture,
        CompactionResult result
    ) =>
        new(
            null,
            fixture.Storage,
            workspaceSessionService: fixture.Service,
            tokenBudgetService: fixture.Budget,
            compactionService: new StaticCompactionService(result),
            sessionOperationCoordinator: new SessionOperationCoordinator()
        );

    private static async Task<List<JsonElement>> WaitForTerminal(
        string terminalKind,
        string sessionId
    )
    {
        var events = new List<JsonElement>();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            string? batch = JsEventDispatcher.DequeueBatchJson(128);
            if (!string.IsNullOrWhiteSpace(batch))
            {
                using var document = JsonDocument.Parse(batch);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (!IsSessionEvent(item, sessionId))
                        continue;
                    events.Add(item.Clone());
                }
                if (events.Any(item => EventKind(item) == terminalKind))
                    return events;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new InvalidOperationException("compact terminal event was not published");
    }

    private static bool IsSessionEvent(JsonElement envelope, string sessionId)
    {
        if (!envelope.TryGetProperty("payload", out var turnEvent))
            return false;
        return turnEvent.ValueKind == JsonValueKind.Object
            && turnEvent.TryGetProperty("sessionId", out var eventSessionId)
            && string.Equals(eventSessionId.GetString(), sessionId, StringComparison.Ordinal);
    }

    private static string EventKind(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("payload", out var turnEvent))
            return string.Empty;
        return turnEvent.TryGetProperty("kind", out var kind)
            ? kind.GetString() ?? string.Empty
            : string.Empty;
    }

    private static JsonElement Payload(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("payload", out var turnEvent))
            throw new InvalidOperationException("turn event payload missing");
        return turnEvent.GetProperty("payload");
    }

    private sealed class StaticCompactionService : ICompactionService
    {
        private readonly CompactionResult _result;

        public StaticCompactionService(CompactionResult result) => _result = result;

        public Task<CompactionResult> CompactAsync(
            string workspaceId,
            string sessionId,
            string? providerId = null,
            string? modelId = null,
            int keepLastGroups = 6,
            CancellationToken ct = default
        ) => Task.FromResult(_result);
    }
}
