using System.Diagnostics;
using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// History authority: directory-shaped sessions, workspace isolation,
/// dual cursors, durable reloads and turn-level rollback.
/// </summary>
public sealed class HistoryAuthorityTests
{
    private static int _requestId = 500;

    private static int NextId() => System.Threading.Interlocked.Increment(ref _requestId);

    private static async Task<string> NewSessionAsync(
        FrameServer server,
        string title = "history",
        string? workspaceId = null
    )
    {
        object payload = workspaceId is null ? new { title } : new { title, workspaceId };
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.new", payload)
        );
        Assert.True(created.Ok);
        string sessionId = created.Result!.Value.GetProperty("sessionId").GetString()!;
        Assert.StartsWith("sess_", sessionId);
        return sessionId;
    }

    [Fact]
    public async Task NewSessionUsesDirectoryShapeAndRichColumns()
    {
        await using var server = new FrameServer();
        var created = await server.SubmitAsync(
            RequestFrame.WithPayload(
                501,
                "session.new",
                new
                {
                    title = "rich",
                    workspaceId = "team-a",
                    providerId = "openai",
                    modelId = "gpt-4o",
                    agentRole = "main",
                }
            )
        );
        Assert.True(created.Ok);
        var root = created.Result!.Value;
        Assert.Equal("team-a", root.GetProperty("workspaceId").GetString());
        Assert.Equal("openai", root.GetProperty("providerId").GetString());
        Assert.Equal("gpt-4o", root.GetProperty("modelId").GetString());

        string sessionId = root.GetProperty("sessionId").GetString()!;
        var fetched = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.get",
                new { sessionId, workspaceId = "team-a" }
            )
        );
        Assert.True(fetched.Ok);
        Assert.Equal("rich", fetched.Result!.Value.GetProperty("title").GetString());
        Assert.Equal("team-a", fetched.Result!.Value.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task ExplicitTitleWriteLocksOnDemand()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, "draft");
        var written = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.title",
                new
                {
                    sessionId,
                    title = "final",
                    titleLocked = true
                }
            )
        );
        Assert.True(written.Ok);
        Assert.True(written.Result!.Value.GetProperty("titleLocked").GetBoolean());

        var renamed = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.rename",
                new { sessionId, title = "changed" }
            )
        );
        Assert.False(renamed.Ok);

        var fetched = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.get", new { sessionId })
        );
        Assert.True(fetched.Ok);
        Assert.Equal("final", fetched.Result!.Value.GetProperty("title").GetString());
        Assert.True(fetched.Result!.Value.GetProperty("titleLocked").GetBoolean());
    }

    [Fact]
    public async Task WorkspaceScopeNeverSweepsAll()
    {
        await using var server = new FrameServer();
        string first = await NewSessionAsync(server, "one", "team-a");
        string second = await NewSessionAsync(server, "two", "team-b");

        var listedA = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.list", new { workspaceId = "team-a" })
        );
        Assert.True(listedA.Ok);
        var idsA = listedA
            .Result!.Value.GetProperty("sessions")
            .EnumerateArray()
            .Select(e => e.GetProperty("sessionId").GetString())
            .ToList();
        Assert.Contains(first, idsA);
        Assert.DoesNotContain(second, idsA);

        var listedDefault = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.list", new { })
        );
        Assert.True(listedDefault.Ok);
        var idsDefault = listedDefault
            .Result!.Value.GetProperty("sessions")
            .EnumerateArray()
            .Select(e => e.GetProperty("sessionId").GetString())
            .ToList();
        Assert.DoesNotContain(first, idsDefault);
        Assert.DoesNotContain(second, idsDefault);
        Assert.True(server.Sessions.WorkspaceFallbackCount >= 1);

        var crossed = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.get",
                new { sessionId = first, workspaceId = "team-b" }
            )
        );
        Assert.False(crossed.Ok);
        Assert.Equal("NOT_FOUND", crossed.Error!.Code);
    }

    [Fact]
    public async Task HistoryCursorAcceptsIndexAndMessageId()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "agent.prompt", new { sessionId, text = "first" })
        );
        await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "agent.prompt", new { sessionId, text = "second" })
        );

        var firstPage = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.history", new { sessionId, limit = 1 })
        );
        Assert.True(firstPage.Ok);
        var items = firstPage.Result!.Value.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        string messageId = items[0].GetProperty("id").GetString()!;
        Assert.StartsWith("msg_", messageId);
        Assert.Equal(4, firstPage.Result!.Value.GetProperty("total").GetInt64());

        var byId = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.history",
                new
                {
                    sessionId,
                    cursor = messageId,
                    limit = 50
                }
            )
        );
        Assert.True(byId.Ok);
        var rest = byId.Result!.Value.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, rest.Count);
        Assert.DoesNotContain(rest, e => e.GetProperty("id").GetString() == messageId);

        var byIndex = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.history",
                new
                {
                    sessionId,
                    cursor = "1",
                    limit = 50
                }
            )
        );
        Assert.True(byIndex.Ok);
        Assert.Equal(
            rest.Select(e => e.GetProperty("id").GetString()).ToList(),
            byIndex
                .Result!.Value.GetProperty("items")
                .EnumerateArray()
                .Select(e => e.GetProperty("id").GetString())
                .ToList()
        );
    }

    [Fact]
    public async Task HistoryLimitClampsAndReportsTotal()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "agent.prompt", new { sessionId, text = "hello" })
        );

        var oversized = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.history", new { sessionId, limit = 5000 })
        );
        Assert.True(oversized.Ok);
        Assert.True(oversized.Result!.Value.GetProperty("items").GetArrayLength() <= 200);
        Assert.Equal(2, oversized.Result!.Value.GetProperty("total").GetInt64());
    }

    [Fact]
    public void SessionsKeepIndependentCursors()
    {
        var ledger = new SessionLedger();
        ledger.EnsureSession("sess_20261108_aaaaaaaa", "left", "default");
        ledger.EnsureSession("sess_20261108_bbbbbbbb", "right", "default");
        ledger.Append("sess_20261108_aaaaaaaa", "user", "l1", "turn-l1");
        ledger.Append("sess_20261108_aaaaaaaa", "user", "l2", "turn-l2");
        ledger.Append("sess_20261108_bbbbbbbb", "user", "r1", "turn-r1");

        var left = ledger.ReadPage("sess_20261108_aaaaaaaa", "0", 50);
        var right = ledger.ReadPage("sess_20261108_bbbbbbbb", "0", 50);
        Assert.Equal(2, left.Items.Count);
        Assert.Single(right.Items);
        Assert.Equal(1, left.Items[0].Index);
        Assert.Equal(1, right.Items[0].Index);
    }

    [Fact]
    public async Task ReloadKeepsHistoryAndCountsBadLines()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-history-" + Guid.NewGuid().ToString("N")
        );
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server, "durable");
        await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "agent.prompt",
                new { sessionId, text = "persist me" }
            )
        );
        server.SaveSessions(directory);
        File.AppendAllText(Path.Combine(directory, sessionId + ".messages.jsonl"), "not-json{\n");

        await using var revived = new FrameServer();
        var report = revived.LoadSessionsWithReport(directory);
        Assert.Equal(1, report.Sessions);
        Assert.True(report.Messages >= 2);
        Assert.Equal(1, report.SkippedLines);

        var page = await revived.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        Assert.True(page.Result!.Value.GetProperty("items").GetArrayLength() >= 2);
        Assert.Equal(
            page.Result!.Value.GetProperty("items").GetArrayLength(),
            page.Result!.Value.GetProperty("total").GetInt64()
        );
    }

    [Fact]
    public async Task PreviousGenerationFileSeedsDirectoryTwin()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-legacy-" + Guid.NewGuid().ToString("N")
        );
        string legacyId = Guid.NewGuid().ToString("N");
        var ledger = new SessionLedger();
        ledger.EnsureSession(legacyId, "legacy", "default");
        ledger.Append(legacyId, "user", "hello", "turn-legacy");
        JsonlHistoryFile.Save(directory, legacyId, "legacy", ledger.Snapshot(legacyId));

        await using var server = new FrameServer();
        var report = server.LoadSessionsWithReport(directory);
        Assert.Equal(1, report.Sessions);
        Assert.Equal(1, report.Aliases);

        var oldRead = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.history",
                new { sessionId = legacyId, limit = 50 }
            )
        );
        Assert.True(oldRead.Ok);
        Assert.True(oldRead.Result!.Value.GetProperty("items").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task WorkingDirectoryChangeNeverReachesTheLedger()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "agent.prompt", new { sessionId, text = "stay" })
        );
        int before = server.Sessions.Count(sessionId);

        var moved = await server.SubmitAsync(
            RequestFrame.WithPayload(
                NextId(),
                "session.setCwd",
                new { sessionId, cwd = "/tmp/work" }
            )
        );
        Assert.True(moved.Ok);
        Assert.Equal("/tmp/work", moved.Result!.Value.GetProperty("cwd").GetString());
        Assert.Equal(before, server.Sessions.Count(sessionId));

        string directory = Path.Combine(
            Path.GetTempPath(),
            "engine-cwd-" + Guid.NewGuid().ToString("N")
        );
        server.SaveSessions(directory);
        await using var revived = new FrameServer();
        revived.LoadSessions(directory);
        Assert.Equal(before, revived.Sessions.Count(sessionId));
    }

    [Fact]
    public async Task UndoRollbackRemovesWholeTurnWithMessageIds()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "agent.prompt", new { sessionId, text = "turn one" })
        );
        int before = server.Sessions.Count(sessionId);
        Assert.Equal(2, before);

        var rolled = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "tool.undoRollback", new { sessionId })
        );
        Assert.True(rolled.Ok);
        Assert.Equal(0, server.Sessions.Count(sessionId));
        Assert.Equal(2, rolled.Result!.Value.GetProperty("removedCount").GetInt32());
        Assert.Equal(2, rolled.Result!.Value.GetProperty("removedMessageIds").GetArrayLength());
    }

    [Fact]
    public async Task HistoryItemsCarryParityColumns()
    {
        await using var server = new FrameServer();
        string sessionId = await NewSessionAsync(server);
        server.Sessions.Append(
            sessionId,
            "assistant",
            "reasoned answer",
            "turn-rich",
            messageId: null,
            toolCallId: "call_demo",
            name: "read_file",
            inputTokens: 12,
            outputTokens: 34,
            cachedTokens: 5,
            reasoningContent: "why this answer",
            errorCode: null
        );

        var page = await server.SubmitAsync(
            RequestFrame.WithPayload(NextId(), "session.history", new { sessionId, limit = 50 })
        );
        Assert.True(page.Ok);
        var items = page.Result!.Value.GetProperty("items").EnumerateArray().ToList();
        var rich = items.First(e => e.GetProperty("role").GetString() == "assistant");
        Assert.StartsWith("msg_", rich.GetProperty("id").GetString());
        Assert.Equal("call_demo", rich.GetProperty("toolCallId").GetString());
        Assert.Equal("reasoned answer", rich.GetProperty("text").GetString());
        Assert.Equal("why this answer", rich.GetProperty("reasoningContent").GetString());
        Assert.Equal(12, rich.GetProperty("inputTokens").GetInt64());
        Assert.Equal(34, rich.GetProperty("outputTokens").GetInt64());
        Assert.Equal(5, rich.GetProperty("cachedTokens").GetInt64());
        Assert.True(rich.TryGetProperty("createdAtUtc", out _));
    }

    [Fact]
    public async Task SystemInfoExposesRoutingProbe()
    {
        await using var server = new FrameServer();
        var info = await server.SubmitAsync(RequestFrame.WithPayload(NextId(), "system.info"));
        Assert.True(info.Ok);
        Assert.True(info.Result!.Value.TryGetProperty("routing", out var routing));
        Assert.True(routing.TryGetProperty("enabledBatches", out _));
    }

    [Fact]
    public void PagedHistoryStaysFastWithCorrectTotal()
    {
        var ledger = new SessionLedger();
        ledger.EnsureSession(SessionIdentity.NewSessionId(), "perf", "default");
        string sessionId = ledger.List("default")[0].SessionId;
        for (int i = 0; i < 500; i++)
        {
            ledger.Append(sessionId, "user", "message " + i, "turn-" + i);
        }
        var timer = Stopwatch.StartNew();
        var page = ledger.ReadPage(sessionId, "0", 50, out long total);
        timer.Stop();
        Assert.Equal(500, total);
        Assert.Equal(50, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"paged read took {timer.Elapsed}");
    }
}
