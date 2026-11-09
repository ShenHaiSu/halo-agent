using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Sunk route table: session, turn, tool confirmation, usage and asset handlers
/// backed by engine ledgers. Read-only calls bypass the frame write gate;
/// mutating calls hold it while touching inner ledger locks.
/// The cancel entry never takes the write gate so it stays the fastest path.
/// </summary>
internal static class EngineRoutes
{
    public static void RegisterInto(
        Dictionary<string, FrameHandler> routes,
        EngineState state,
        SessionLedger sessions,
        TurnCoordinator turns,
        UsageLedger usage,
        AttachmentIndex attachments,
        IEventJournal events,
        TurnQueue? turnQueue = null,
        ConfigAssetLedger? assets = null
    )
    {
        ConfigAssetLedger store = assets ?? new ConfigAssetLedger();
        routes["system.ping"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { pong = true }));

        routes["system.info"] = (request, context, token) =>
        {
            var metrics = state.CancelMetrics();
            long compactRejects = state.CompactRejectCount();
            int queueDepth = turnQueue is null ? 0 : turnQueue.TotalDepth();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        version = ContractCatalog.ContractVersion,
                        contractVersion = ContractCatalog.ContractVersion,
                        bridgeVersion = ContractCatalog.BridgeProtocolVersion,
                        queueBound = FrameServer.QueueBound,
                        journalCapacity = events.Capacity,
                        routing = new
                        {
                            enabledBatches = ReadEnabledBatches(),
                            defaultFrame = store.FrameDefault,
                        },
                        cancel = new
                        {
                            count = metrics.Count,
                            totalMs = metrics.TotalMs,
                            maxMs = metrics.MaxMs,
                            averageMs = metrics.AverageMs,
                        },
                        compactRejects,
                        queueDepth,
                    }
                )
            );
        };

        routes["state.snapshot"] = (request, context, token) =>
        {
            var current = usage.Query();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        activeSessionId = state.ActiveSessionId,
                        mode = state.Mode,
                        personaId = state.PersonaId,
                        maxTurns = state.MaxTurns,
                        latestSequence = events.LatestSequence,
                        inputTokens = current.InputTokens,
                        outputTokens = current.OutputTokens,
                    }
                )
            );
        };

        routes["state.mode"] = (request, context, token) =>
        {
            string mode = FrameServer.ReadString(request.Payload, "mode");
            if (!string.IsNullOrWhiteSpace(mode))
            {
                state.Mode = mode;
            }
            events.Append("state.changed", new { mode = state.Mode });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { mode = state.Mode }));
        };

        routes["state.persona"] = (request, context, token) =>
        {
            string persona = FrameServer.ReadString(request.Payload, "personaId");
            if (string.IsNullOrWhiteSpace(persona))
            {
                persona = FrameServer.ReadString(request.Payload, "persona");
            }
            if (!string.IsNullOrWhiteSpace(persona))
            {
                state.PersonaId = persona;
            }
            return Task.FromResult(
                ResponseFrame.Success(request.Id, new { personaId = state.PersonaId })
            );
        };

        routes["state.maxTurns"] = (request, context, token) =>
        {
            if (TryReadInt(request.Payload, "maxTurns", out int value) && value > 0)
            {
                state.MaxTurns = Math.Clamp(value, 1, 100);
            }
            return Task.FromResult(
                ResponseFrame.Success(request.Id, new { maxTurns = state.MaxTurns })
            );
        };

        routes["session.new"] = (request, context, token) =>
        {
            string title = FrameServer.ReadString(request.Payload, "title");
            string workspaceId =
                ReadOptionalString(request.Payload, "workspaceId")
                ?? SessionIdentity.DefaultWorkspace;
            string? parentSessionId = ReadOptionalString(request.Payload, "parentSessionId");
            string? providerId = ReadOptionalString(request.Payload, "providerId");
            string? modelId = ReadOptionalString(request.Payload, "modelId");
            string agentRole = ReadOptionalString(request.Payload, "agentRole") ?? "main";
            string sessionId = SessionIdentity.NewSessionId();
            sessions.EnsureSession(
                sessionId,
                string.IsNullOrWhiteSpace(title) ? "New session" : title,
                workspaceId,
                parentSessionId,
                providerId,
                modelId,
                agentRole
            );
            state.TryRememberSession(
                sessionId,
                string.IsNullOrWhiteSpace(title) ? "New session" : title
            );
            state.ActiveSessionId = sessionId;
            events.Append(
                "session.updated",
                new
                {
                    sessionId,
                    reason = "created",
                    workspaceId = sessions.ResolveWorkspace(workspaceId)
                }
            );
            sessions.TryGetDescriptor(sessionId, out var created);
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    DescribeSession(sessionId, created, sessions.Count(sessionId))
                )
            );
        };

        routes["session.list"] = (request, context, token) =>
        {
            string? workspaceId = ReadOptionalString(request.Payload, "workspaceId");
            var items = sessions
                .List(workspaceId)
                .Select(d => DescribeSession(d.SessionId, d, sessions.Count(d.SessionId)))
                .ToList();
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessions = items }));
        };

        routes["session.get"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string? workspaceId = ReadOptionalString(request.Payload, "workspaceId");
            if (!sessions.TryGetDescriptor(sessionId, out var descriptor) || descriptor is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            if (
                !string.IsNullOrWhiteSpace(workspaceId)
                && !string.Equals(descriptor.WorkspaceId, workspaceId, StringComparison.Ordinal)
            )
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "Session is unknown in this workspace."
                    )
                );
            }
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    DescribeSession(sessionId, descriptor, sessions.Count(sessionId))
                )
            );
        };

        routes["session.resume"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (!sessions.TryGetTitle(sessionId, out string? title))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            state.ActiveSessionId = sessionId;
            events.Append("session.updated", new { sessionId, reason = "resumed" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId, title }));
        };

        routes["session.rename"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string title = FrameServer.ReadString(request.Payload, "title");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "sessionId and title are required."
                    )
                );
            }
            if (!sessions.Rename(sessionId, title))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            events.Append("session.updated", new { sessionId, reason = "renamed" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId, title }));
        };

        // Explicit title write from the frozen contract: sets the title and
        // records the lock flag in one call. Automatic renames never happen;
        // only this entry flips the lock.
        routes["session.title"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string title = FrameServer.ReadString(request.Payload, "title");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            if (!sessions.TryGetDescriptor(sessionId, out var descriptor) || descriptor is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (descriptor.TitleLocked)
                {
                    return Task.FromResult(
                        ResponseFrame.Fail(
                            request.Id,
                            "INVALID_PARAMS",
                            "Title is locked for this session."
                        )
                    );
                }
                if (!sessions.Rename(sessionId, title))
                {
                    return Task.FromResult(
                        ResponseFrame.Fail(
                            request.Id,
                            "INTERNAL",
                            "Title write failed.",
                            "TITLE_SAVE_FAILED"
                        )
                    );
                }
                bool locked = TryReadBool(request.Payload, "titleLocked", descriptor.TitleLocked);
                sessions.SetTitleLocked(sessionId, locked);
                events.Append(
                    "session.updated",
                    new
                    {
                        sessionId,
                        reason = "title",
                        title
                    }
                );
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            sessionId,
                            title,
                            titleLocked = locked
                        }
                    )
                );
            }
            var history = sessions.Snapshot(sessionId);
            var firstUser = history.FirstOrDefault(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
            );
            if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Text))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "First user message is missing.",
                        "NO_USER_MESSAGE"
                    )
                );
            }
            if (descriptor.TitleLocked)
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            sessionId,
                            title = descriptor.Title,
                            titleLocked = true
                        }
                    )
                );
            }
            if (context.Gateway is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "No provider is available.",
                        "NO_PROVIDER"
                    )
                );
            }
            string derived = firstUser.Text.Trim();
            if (derived.Length > 12)
            {
                derived = derived.Substring(0, 12);
            }
            if (string.IsNullOrWhiteSpace(derived))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INTERNAL",
                        "Generated title is empty.",
                        "TITLE_PARSE_EMPTY"
                    )
                );
            }
            if (!sessions.Rename(sessionId, derived))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INTERNAL",
                        "Title write failed.",
                        "TITLE_SAVE_FAILED"
                    )
                );
            }
            sessions.SetTitleLocked(sessionId, true);
            events.Append(
                "session.updated",
                new
                {
                    sessionId,
                    reason = "title",
                    title = derived
                }
            );
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        title = derived,
                        titleLocked = true
                    }
                )
            );
        };

        routes["session.delete"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    var tree = sessions.ListTreeSessions(sessionId);
                    var treeSet = new HashSet<string>(
                        tree.Count > 0 ? tree : new[] { sessionId },
                        StringComparer.Ordinal
                    );
                    foreach (string cancelledTurn in turns.CancelTreeBySessions(treeSet.Contains))
                    {
                        turns.RemoveTimeout(cancelledTurn);
                    }
                    foreach (string member in treeSet)
                    {
                        sessions.TryCancelCompaction(member);
                        turnQueue?.Clear(member);
                    }
                    foreach (string member in treeSet)
                    {
                        sessions.Delete(member);
                    }
                }
                catch
                {
                    sessions.Delete(sessionId);
                }
            }
            else
            {
                sessions.Delete(sessionId);
            }
            if (string.Equals(state.ActiveSessionId, sessionId, StringComparison.Ordinal))
            {
                state.ActiveSessionId = null;
            }
            events.Append("session.updated", new { sessionId, reason = "deleted" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId }));
        };

        routes["session.clear"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            sessions.Clear(sessionId);
            events.Append("session.updated", new { sessionId, reason = "cleared" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId }));
        };

        routes["session.history"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string cursor = FrameServer.ReadString(request.Payload, "cursor");
            int limit = TryReadInt(request.Payload, "limit", out int parsed) ? parsed : 50;
            var page = sessions.ReadPage(sessionId, cursor, limit, out long total);
            var items = page.Items.Select(DescribeMessage).ToList();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        items,
                        nextCursor = page.NextCursor,
                        hasMore = page.HasMore,
                        total,
                        messageCount = total,
                    }
                )
            );
        };

        routes["session.compact"] = async (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.");
            }
            bool cancelAsked = TryReadBool(request.Payload, "cancel", defaultValue: false);
            if (cancelAsked)
            {
                bool stopped = sessions.TryCancelCompaction(sessionId);
                if (stopped)
                {
                    events.Append(
                        "session.updated",
                        new { sessionId, reason = "compactCancelled" }
                    );
                    return ResponseFrame.Success(request.Id, new { sessionId, cancelled = true });
                }
                return ResponseFrame.Fail(
                    request.Id,
                    "NOT_FOUND",
                    "No running compaction to cancel."
                );
            }
            bool compactEnabled =
                context.TurnOptions is null || context.TurnOptions.EnableTrueCompaction;
            if (!compactEnabled || EnvSwitchOff("HALO_ENGINE_COMPACT"))
            {
                if (!sessions.TryBeginCompaction(sessionId))
                {
                    state.RecordCompactReject();
                    return ResponseFrame.Fail(
                        request.Id,
                        "COMPACTING",
                        "Compaction is already running."
                    );
                }
                string stubSummary = $"Compacted {sessions.Count(sessionId)} messages.";
                sessions.EndCompaction(sessionId, stubSummary);
                events.Append("session.compacted", new { sessionId, summary = stubSummary });
                return ResponseFrame.Success(request.Id, new { sessionId, summary = stubSummary });
            }
            if (sessions.IsCompacting(sessionId))
            {
                state.RecordCompactReject();
                return ResponseFrame.Fail(
                    request.Id,
                    "COMPACTING",
                    "Compaction is already running."
                );
            }
            if (!sessions.TryBeginCompaction(sessionId))
            {
                state.RecordCompactReject();
                return ResponseFrame.Fail(
                    request.Id,
                    "COMPACTING",
                    "Compaction is already running."
                );
            }
            int keepGroups = TryReadInt(request.Payload, "keepLastGroups", out int keepParsed)
                ? keepParsed
                : (context.TurnOptions?.CompactKeepGroups ?? 6);
            if (keepGroups <= 0)
            {
                keepGroups = 6;
            }
            events.Append("session.updated", new { sessionId, reason = "compactStarted" });
            var rows = sessions.Snapshot(sessionId);
            if (rows.Count == 0)
            {
                sessions.EndCompaction(sessionId, "Empty session; nothing to compact.");
                string emptySummary = "Empty session; nothing to compact.";
                events.Append("session.compacted", new { sessionId, summary = emptySummary });
                return ResponseFrame.Success(request.Id, new { sessionId, summary = emptySummary });
            }
            string summaryText;
            try
            {
                summaryText = await BuildCompactionSummaryAsync(
                        sessions,
                        context,
                        sessionId,
                        rows,
                        keepGroups,
                        token
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sessions.TryCancelCompaction(sessionId);
                events.Append(
                    "turn.failed",
                    new
                    {
                        sessionId,
                        reason = "CANCELLED",
                        hint = "Compaction was cancelled."
                    }
                );
                return ResponseFrame.Fail(request.Id, "CANCELLED", "Compaction was cancelled.");
            }
            catch (TimeoutException)
            {
                sessions.TryCancelCompaction(sessionId);
                events.Append(
                    "turn.failed",
                    new
                    {
                        sessionId,
                        reason = "TOOL_TIMEOUT",
                        hint = "TITLE_TIMEOUT"
                    }
                );
                return ResponseFrame.Fail(
                    request.Id,
                    "TOOL_TIMEOUT",
                    "Compaction summary timed out.",
                    "TITLE_TIMEOUT"
                );
            }
            catch (Exception ex)
            {
                sessions.TryCancelCompaction(sessionId);
                events.Append(
                    "turn.failed",
                    new
                    {
                        sessionId,
                        reason = "INTERNAL",
                        hint = TrimHint(ex.Message)
                    }
                );
                return ResponseFrame.Fail(request.Id, "INTERNAL", "Compaction summary failed.");
            }
            if (
                sessions.TryCompactWithSummary(
                    sessionId,
                    summaryText,
                    keepGroups,
                    out int removed,
                    out int kept
                )
            )
            {
                sessions.EndCompaction(sessionId, summaryText);
                events.Append(
                    "session.compacted",
                    new
                    {
                        sessionId,
                        summary = summaryText,
                        removedMessages = removed,
                        keptMessages = kept
                    }
                );
                return ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        summary = summaryText,
                        removedMessages = removed,
                        keptMessages = kept
                    }
                );
            }
            sessions.EndCompaction(sessionId, summaryText);
            events.Append("session.compacted", new { sessionId, summary = summaryText });
            return ResponseFrame.Success(request.Id, new { sessionId, summary = summaryText });
        };

        // The working directory is a host-shell concern: this entry only forwards
        // the notification to event subscribers and never writes the ledger,
        // so a restart does not resurrect a stale directory.
        routes["session.setCwd"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string cwd = FrameServer.ReadString(request.Payload, "cwd");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            events.Append(
                "session.updated",
                new
                {
                    sessionId,
                    reason = "cwd",
                    cwd
                }
            );
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId, cwd }));
        };

        routes["agent.prompt"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string text = FrameServer.ReadString(request.Payload, "text");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "sessionId and text are required."
                    )
                );
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "Prompt text is required.",
                        "EMPTY_PROMPT"
                    )
                );
            }
            if (!sessions.TryGetTitle(sessionId, out _))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "Session is unknown.",
                        "INVALID_SESSION"
                    )
                );
            }
            if (sessions.IsCompacting(sessionId))
            {
                state.RecordCompactReject();
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "COMPACTING",
                        "Wait for compaction or stop it first."
                    )
                );
            }
            string? callerKey = ReadOptionalString(request.Payload, "requestId");
            if (
                !string.IsNullOrWhiteSpace(callerKey)
                && turns.TryGetRequestTurn(callerKey!, out string? replayed)
                && !string.IsNullOrWhiteSpace(replayed)
            )
            {
                return Task.FromResult(
                    ResponseFrame.Success(request.Id, new { turnId = replayed })
                );
            }
            string turnId = Guid.NewGuid().ToString("N");
            if (!turns.TryStart(sessionId, turnId, request.Id, out string activeRequest))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "TURN_IN_PROGRESS",
                        "Another turn is running.",
                        activeRequest
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(callerKey))
            {
                turns.RememberRequestTurn(callerKey!, turnId);
            }
            if (context.Gateway is null)
            {
                return Task.FromResult(
                    RunEchoTurn(
                        request,
                        sessions,
                        turns,
                        usage,
                        events,
                        attachments,
                        turnId,
                        sessionId,
                        text
                    )
                );
            }
            string? providerId = ReadOptionalString(request.Payload, "providerId");
            string? modelId = ReadOptionalString(request.Payload, "modelId");
            string workspaceId =
                sessions.TryGetDescriptor(sessionId, out var known) && known is not null
                    ? known.WorkspaceId
                    : SessionIdentity.DefaultWorkspace;
            sessions.Append(sessionId, "user", text, turnId);
            events.Append("turn.started", new { turnId, sessionId });
            var runner = new TurnRunner(context, sessions, turns, usage);
            CancellationToken turnToken = context.TurnToken(turnId);
            _ = Task.Run(
                () =>
                    runner.RunPromptTurnAsync(
                        sessionId,
                        turnId,
                        text,
                        providerId,
                        modelId,
                        workspaceId,
                        turnToken
                    ),
                CancellationToken.None
            );
            return Task.FromResult(ResponseFrame.Success(request.Id, new { turnId }));
        };

        routes["agent.answer"] = (request, context, token) =>
        {
            string turnId = FrameServer.ReadString(request.Payload, "turnId");
            bool approved =
                !TryReadBool(request.Payload, "rejected")
                && TryReadBool(request.Payload, "approved", defaultValue: true);
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "turnId is required.")
                );
            }
            string? toolCallId = ReadOptionalString(request.Payload, "toolCallId");
            if (!turns.TryTakeConfirmation(turnId, toolCallId, out var pending))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "No pending confirmation for this turn."
                    )
                );
            }
            string? ticketId = ReadOptionalString(request.Payload, "ticketId");
            if (
                !string.IsNullOrWhiteSpace(ticketId)
                && !string.IsNullOrWhiteSpace(pending!.TicketId)
                && !string.Equals(ticketId!.Trim(), pending.TicketId, StringComparison.Ordinal)
            )
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "Ticket does not match the pending confirmation.",
                        "ASK_TICKET_MISMATCH"
                    )
                );
            }
            string? answersJson =
                ReadOptionalString(request.Payload, "answers")
                ?? ReadOptionalString(request.Payload, "answersJson");
            if (!turns.ResolveConfirmation(turnId, toolCallId, approved, answersJson))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "No pending confirmation for this turn."
                    )
                );
            }
            if (!turns.TrySessionOf(turnId, out string? sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Turn is unknown.")
                );
            }
            // Echo-path confirmations ("edit.apply") have no runner waiter, so the
            // route closes the turn here. True-loop gates are resumed by the
            // waiting runner, which owns the follow-up events.
            bool echoStyle = string.Equals(
                pending!.ToolName,
                "edit.apply",
                StringComparison.Ordinal
            );
            if (echoStyle)
            {
                if (approved)
                {
                    events.Append("tool.end", new { turnId, ok = true });
                    events.Append("turn.done", new { turnId, sessionId });
                    turns.Finish(turnId, TurnOutcome.Done);
                }
                else
                {
                    events.Append(
                        "turn.cancelled",
                        new
                        {
                            turnId,
                            sessionId,
                            reason = "rejected"
                        }
                    );
                    turns.Finish(turnId, TurnOutcome.Cancelled);
                }
            }
            return Task.FromResult(ResponseFrame.Success(request.Id, new { turnId, approved }));
        };

        routes["agent.retry"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            if (sessions.IsCompacting(sessionId))
            {
                state.RecordCompactReject();
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "COMPACTING",
                        "Wait for compaction or stop it first."
                    )
                );
            }
            string? active = turns.ActiveTurnOf(sessionId);
            if (active is not null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "TURN_IN_PROGRESS",
                        "Another turn is running.",
                        active
                    )
                );
            }
            var last = sessions.Snapshot(sessionId).LastOrDefault(m => m.Role == "user");
            if (last is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "No user message to retry.")
                );
            }
            string turnId = Guid.NewGuid().ToString("N");
            if (!turns.TryStart(sessionId, turnId, request.Id, out string retryBusy))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "TURN_IN_PROGRESS",
                        "Another turn is running.",
                        retryBusy
                    )
                );
            }
            try
            {
                usage.MarkRetry();
            }
            catch { }
            if (context.Gateway is null)
            {
                events.Append(
                    "turn.started",
                    new
                    {
                        turnId,
                        sessionId,
                        retriedFrom = last.TurnId
                    }
                );
                events.Append("turn.done", new { turnId, sessionId });
                turns.Finish(turnId, TurnOutcome.Done);
                return Task.FromResult(
                    ResponseFrame.Success(request.Id, new { turnId, retriedFrom = last.TurnId })
                );
            }
            string retryWorkspace =
                sessions.TryGetDescriptor(sessionId, out var retryKnown) && retryKnown is not null
                    ? retryKnown.WorkspaceId
                    : SessionIdentity.DefaultWorkspace;
            events.Append(
                "turn.started",
                new
                {
                    turnId,
                    sessionId,
                    retriedFrom = last.TurnId
                }
            );
            var retryRunner = new TurnRunner(context, sessions, turns, usage);
            CancellationToken retryToken = context.TurnToken(turnId);
            string retryText = last.Text;
            _ = Task.Run(
                () =>
                    retryRunner.RunPromptTurnAsync(
                        sessionId,
                        turnId,
                        retryText,
                        null,
                        null,
                        retryWorkspace,
                        retryToken
                    ),
                CancellationToken.None
            );
            return Task.FromResult(
                ResponseFrame.Success(request.Id, new { turnId, retriedFrom = last.TurnId })
            );
        };

        routes["agent.queue"] = (request, context, token) =>
        {
            string queueSession = FrameServer.ReadString(request.Payload, "sessionId");
            string queueText = FrameServer.ReadString(request.Payload, "text");
            if (string.IsNullOrWhiteSpace(queueSession) || string.IsNullOrWhiteSpace(queueText))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "sessionId and text are required."
                    )
                );
            }
            if (
                turnQueue is null
                || (context.TurnOptions is not null && !context.TurnOptions.EnableTrueQueue)
                || EnvSwitchOff("HALO_ENGINE_QUEUE")
            )
            {
                return Task.FromResult(ResponseFrame.Success(request.Id, new { queued = true }));
            }
            if (!sessions.TryGetTitle(queueSession, out _))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "Session is unknown.",
                        "INVALID_SESSION"
                    )
                );
            }
            string queueWorkspace = SessionIdentity.DefaultWorkspace;
            try
            {
                if (
                    sessions.TryGetDescriptor(queueSession, out var queueKnown)
                    && queueKnown is not null
                )
                {
                    queueWorkspace = string.IsNullOrWhiteSpace(queueKnown.WorkspaceId)
                        ? SessionIdentity.DefaultWorkspace
                        : queueKnown.WorkspaceId;
                }
            }
            catch { }
            int depth = turnQueue.Enqueue(
                queueSession,
                queueText,
                ReadOptionalString(request.Payload, "providerId"),
                ReadOptionalString(request.Payload, "modelId"),
                queueWorkspace,
                ReadOptionalString(request.Payload, "requestId")
            );
            events.Append(
                "session.updated",
                new
                {
                    sessionId = queueSession,
                    reason = "queued",
                    depth
                }
            );
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        queued = true,
                        sessionId = queueSession,
                        depth
                    }
                )
            );
        };
        routes["agent.drain"] = (request, context, token) =>
        {
            string drainSession = FrameServer.ReadString(request.Payload, "sessionId");
            if (string.IsNullOrWhiteSpace(drainSession))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            if (
                turnQueue is null
                || (context.TurnOptions is not null && !context.TurnOptions.EnableTrueQueue)
                || EnvSwitchOff("HALO_ENGINE_QUEUE")
            )
            {
                return Task.FromResult(ResponseFrame.Success(request.Id, new { drained = true }));
            }
            if (!turnQueue.TryDequeue(drainSession, out var head) || head is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Queue is empty.")
                );
            }
            if (turns.ActiveTurnOf(drainSession) is not null)
            {
                turnQueue.Enqueue(
                    head.SessionId,
                    head.Text,
                    head.ProviderId,
                    head.ModelId,
                    head.WorkspaceId,
                    head.RequestId
                );
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "TURN_IN_PROGRESS",
                        "Another turn is running; entry kept."
                    )
                );
            }
            if (sessions.IsCompacting(drainSession))
            {
                turnQueue.Enqueue(
                    head.SessionId,
                    head.Text,
                    head.ProviderId,
                    head.ModelId,
                    head.WorkspaceId,
                    head.RequestId
                );
                state.RecordCompactReject();
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "COMPACTING",
                        "Wait for compaction or stop it first."
                    )
                );
            }
            string drainedTurn = Guid.NewGuid().ToString("N");
            if (!turns.TryStart(drainSession, drainedTurn, request.Id, out string busyHint))
            {
                turnQueue.Enqueue(
                    head.SessionId,
                    head.Text,
                    head.ProviderId,
                    head.ModelId,
                    head.WorkspaceId,
                    head.RequestId
                );
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "TURN_IN_PROGRESS",
                        "Another turn is running.",
                        busyHint
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(head.RequestId))
            {
                turns.RememberRequestTurn(head.RequestId!, drainedTurn);
            }
            sessions.Append(drainSession, "user", head.Text, drainedTurn);
            events.Append(
                "turn.started",
                new
                {
                    turnId = drainedTurn,
                    sessionId = drainSession,
                    drained = true
                }
            );
            if (context.Gateway is null)
            {
                var drainedReply = RunEchoTurn(
                    request,
                    sessions,
                    turns,
                    usage,
                    events,
                    attachments,
                    drainedTurn,
                    drainSession,
                    head.Text
                );
                return Task.FromResult(drainedReply);
            }
            var drainedRunner = new TurnRunner(context, sessions, turns, usage);
            CancellationToken drainedToken = context.TurnToken(drainedTurn);
            string drainedWorkspace = head.WorkspaceId;
            string drainedText = head.Text;
            string? drainedProvider = head.ProviderId;
            string? drainedModel = head.ModelId;
            _ = Task.Run(
                () =>
                    drainedRunner.RunPromptTurnAsync(
                        drainSession,
                        drainedTurn,
                        drainedText,
                        drainedProvider,
                        drainedModel,
                        drainedWorkspace,
                        drainedToken
                    ),
                CancellationToken.None
            );
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        drained = true,
                        turnId = drainedTurn,
                        sessionId = drainSession,
                        depth = turnQueue.Depth(drainSession)
                    }
                )
            );
        };
        routes["agent.unqueue"] = (request, context, token) =>
        {
            string unqueueSession = FrameServer.ReadString(request.Payload, "sessionId");
            if (string.IsNullOrWhiteSpace(unqueueSession))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            if (
                turnQueue is null
                || (context.TurnOptions is not null && !context.TurnOptions.EnableTrueQueue)
                || EnvSwitchOff("HALO_ENGINE_QUEUE")
            )
            {
                return Task.FromResult(ResponseFrame.Success(request.Id, new { unqueued = true }));
            }
            string? unqueueKey = ReadOptionalString(request.Payload, "requestId");
            if (
                turnQueue.TryRemove(unqueueSession, unqueueKey, out var dropped)
                && dropped is not null
            )
            {
                events.Append(
                    "session.updated",
                    new
                    {
                        sessionId = unqueueSession,
                        reason = "unqueued",
                        depth = turnQueue.Depth(unqueueSession)
                    }
                );
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            unqueued = true,
                            sessionId = unqueueSession,
                            depth = turnQueue.Depth(unqueueSession)
                        }
                    )
                );
            }
            return Task.FromResult(
                ResponseFrame.Fail(request.Id, "NOT_FOUND", "No queued entry matches.")
            );
        };

        routes["tool.undoRollback"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (!sessions.TryRollbackTurn(sessionId, out var removedMessages, out var lastRemoved))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Nothing to roll back.")
                );
            }
            events.Append(
                "session.updated",
                new
                {
                    sessionId,
                    reason = "rollback",
                    removedTurnId = lastRemoved!.TurnId,
                    removedMessageIds = removedMessages.Select(m => m.Id).ToArray(),
                    removedCount = removedMessages.Count,
                }
            );
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        rolledBack = true,
                        removedMessageIds = removedMessages.Select(m => m.Id).ToArray(),
                        removedCount = removedMessages.Count,
                    }
                )
            );
        };

        routes["usage.query"] = (request, context, token) =>
        {
            try
            {
                usage.SetMaxTokens(context.TurnOptions?.BudgetMaxTokens ?? 128000);
            }
            catch { }
            var current = usage.Query();
            var budget = usage.BudgetStatus();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        inputTokens = current.InputTokens,
                        outputTokens = current.OutputTokens,
                        cachedTokens = current.CachedTokens,
                        retryCount = current.RetryCount,
                        maxTokens = budget.MaxTokens,
                        usedTokens = budget.UsedTokens,
                        usagePercentage = budget.UsagePercentage,
                        occupancyState = budget.OccupancyState,
                        usedIsEstimated = budget.UsedIsEstimated,
                    }
                )
            );
        };

        routes["usage.clear"] = (request, context, token) =>
        {
            usage.Clear();
            try
            {
                usage.SetMaxTokens(context.TurnOptions?.BudgetMaxTokens ?? 128000);
            }
            catch { }
            return Task.FromResult(ResponseFrame.Success(request.Id, new { cleared = true }));
        };

        RegisterConfigAssetRoutes(routes, store);

        foreach (string hostOnly in new[] { "host.pickDirectory", "host.openPath" })
        {
            routes[hostOnly] = (request, context, token) =>
                Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "Host-only capability; the desktop shell handles it."
                    )
                );
        }

        _ = attachments;
    }

    /// <summary>
    /// Configuration batch (providers, network, behavior with the default-frame
    /// switch, read-only personas) plus asset reads (skills, memory). Each side
    /// has its own rollback switch so one risky batch reverts to the safe stub
    /// without touching the other. Memory writes intentionally stay on the
    /// desktop chain; the stub reply documents that instead of faking a save.
    /// </summary>
    private static void RegisterConfigAssetRoutes(
        Dictionary<string, FrameHandler> routes,
        ConfigAssetLedger store
    )
    {
        routes["settings.saveProviders"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "settings.saveProviders" }
                    )
                );
            }
            try
            {
                string saved = store.SaveProviders(request.Payload);
                PersistAssets(context, store);
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            saved = true,
                            method = "settings.saveProviders",
                            providers = JsonDocument.Parse(saved).RootElement
                        }
                    )
                );
            }
            catch (LedgerValidationException ex)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", ex.Message)
                );
            }
        };
        routes["settings.setActive"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "settings.setActive" }
                    )
                );
            }
            try
            {
                var picked = store.SaveActive(request.Payload);
                PersistAssets(context, store);
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            saved = true,
                            providerId = picked.ProviderId,
                            modelId = picked.ModelId
                        }
                    )
                );
            }
            catch (LedgerValidationException ex)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", ex.Message)
                );
            }
        };
        routes["settings.saveNetwork"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "settings.saveNetwork" }
                    )
                );
            }
            try
            {
                string saved = store.SaveNetwork(request.Payload);
                PersistAssets(context, store);
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            saved = true,
                            method = "settings.saveNetwork",
                            network = JsonDocument.Parse(saved).RootElement
                        }
                    )
                );
            }
            catch (LedgerValidationException ex)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", ex.Message)
                );
            }
        };
        routes["settings.saveBehavior"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "settings.saveBehavior" }
                    )
                );
            }
            try
            {
                string saved = store.SaveBehavior(request.Payload);
                PersistAssets(context, store);
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new
                        {
                            saved = true,
                            method = "settings.saveBehavior",
                            frameDefault = store.FrameDefault,
                            behavior = JsonDocument.Parse(saved).RootElement
                        }
                    )
                );
            }
            catch (LedgerValidationException ex)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", ex.Message)
                );
            }
        };
        routes["asset.personas.list"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "asset.personas.list" }
                    )
                );
            }
            var items = store.ListPersonas();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        items = items.Select(e => new
                        {
                            name = e.Name,
                            title = e.Title,
                            summary = e.Summary,
                            scope = e.Scope
                        })
                    }
                )
            );
        };
        routes["asset.personas.get"] = (request, context, token) =>
        {
            if (!ConfigBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "asset.personas.get" }
                    )
                );
            }
            string name = FrameServer.ReadString(request.Payload, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = FrameServer.ReadString(request.Payload, "personaId");
            }
            var found = string.IsNullOrWhiteSpace(name) ? null : store.GetPersona(name);
            if (found is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Persona is unknown.")
                );
            }
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        name = found.Name,
                        title = found.Title,
                        summary = found.Summary,
                        scope = found.Scope
                    }
                )
            );
        };
        routes["asset.skills.list"] = (request, context, token) =>
        {
            if (!AssetBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "asset.skills.list" }
                    )
                );
            }
            var items = store.ListSkills();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        items = items.Select(e => new
                        {
                            name = e.Name,
                            title = e.Title,
                            summary = e.Summary,
                            scope = e.Scope
                        })
                    }
                )
            );
        };
        routes["asset.skills.get"] = (request, context, token) =>
        {
            if (!AssetBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "asset.skills.get" }
                    )
                );
            }
            string name = FrameServer.ReadString(request.Payload, "name");
            var found = string.IsNullOrWhiteSpace(name) ? null : store.GetSkill(name);
            if (found is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Skill is unknown.")
                );
            }
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        name = found.Name,
                        title = found.Title,
                        summary = found.Summary,
                        scope = found.Scope
                    }
                )
            );
        };
        routes["asset.memory.list"] = (request, context, token) =>
        {
            if (!AssetBatchOn())
            {
                return Task.FromResult(
                    ResponseFrame.Success(
                        request.Id,
                        new { saved = true, method = "asset.memory.list" }
                    )
                );
            }
            var items = store.ListMemory();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        items = items.Select(e => new
                        {
                            name = e.Name,
                            title = e.Title,
                            summary = e.Summary,
                            scope = e.Scope
                        })
                    }
                )
            );
        };
        routes["asset.memory.save"] = (request, context, token) =>
            Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        saved = true,
                        method = "asset.memory.save",
                        note = "Memory writes stay on the desktop chain."
                    }
                )
            );
    }

    private static bool BatchWanted(string batch)
    {
        string[] enabled = ReadEnabledBatches();
        if (enabled.Length == 0)
        {
            return true;
        }
        return enabled.Contains(batch, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ConfigBatchOn()
    {
        return BatchWanted("B") && !EnvSwitchOff("HALO_ENGINE_CONFIG");
    }

    private static bool AssetBatchOn()
    {
        return BatchWanted("C") && !EnvSwitchOff("HALO_ENGINE_ASSETS");
    }

    private static void PersistAssets(IEngineContext context, ConfigAssetLedger store)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(context.DataRoot))
            {
                store.Save(context.DataRoot);
            }
        }
        catch { }
    }

    internal static bool TryReadInt(JsonElement payload, string name, out int value)
    {
        value = 0;
        if (
            payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var node)
        )
        {
            return false;
        }
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out int parsed))
        {
            value = parsed;
            return true;
        }
        if (
            node.ValueKind == JsonValueKind.String
            && int.TryParse(node.GetString(), out int textParsed)
        )
        {
            value = textParsed;
            return true;
        }
        return false;
    }

    internal static bool TryReadBool(JsonElement payload, string name, bool defaultValue = false)
    {
        if (
            payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var node)
        )
        {
            return defaultValue;
        }
        if (node.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (node.ValueKind == JsonValueKind.False)
        {
            return false;
        }
        if (
            node.ValueKind == JsonValueKind.String
            && bool.TryParse(node.GetString(), out bool parsed)
        )
        {
            return parsed;
        }
        return defaultValue;
    }

    internal const int TruncatePreviewLimit = 2048;

    internal static string TruncateForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= TruncatePreviewLimit)
        {
            return text;
        }
        return text.Substring(0, TruncatePreviewLimit);
    }

    internal static IReadOnlyList<AttachmentReference> RegisterAttachments(
        JsonElement payload,
        string sessionId,
        AttachmentIndex attachments
    )
    {
        var registered = new List<AttachmentReference>();
        if (
            payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("attachments", out var node)
            || node.ValueKind != JsonValueKind.Array
        )
        {
            return registered;
        }
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string digest = ReadEntryString(entry, "digest") ?? string.Empty;
            string relPath = ReadEntryString(entry, "relPath") ?? string.Empty;
            string fileName = ReadEntryString(entry, "fileName") ?? "attachment.bin";
            string mime = ReadEntryString(entry, "mime") ?? string.Empty;
            long byteSize = ReadEntrySize(entry);
            if (string.IsNullOrWhiteSpace(digest) && string.IsNullOrWhiteSpace(relPath))
            {
                continue;
            }
            registered.Add(
                attachments.Register(digest, sessionId, fileName, byteSize, mime, relPath)
            );
        }
        return registered;
    }

    internal static object DescribeSession(
        string sessionId,
        SessionDescriptor? descriptor,
        long messageCount
    )
    {
        if (descriptor is null)
        {
            return new { sessionId, messageCount };
        }
        return new
        {
            sessionId,
            title = descriptor.Title,
            workspaceId = descriptor.WorkspaceId,
            parentSessionId = descriptor.ParentSessionId,
            providerId = descriptor.ProviderId,
            modelId = descriptor.ModelId,
            agentRole = descriptor.AgentRole,
            isPinned = descriptor.IsPinned,
            titleLocked = descriptor.TitleLocked,
            messageCount,
            total = messageCount,
            createdAtUtc = descriptor.CreatedAtUtc,
            updatedAtUtc = descriptor.UpdatedAtUtc,
        };
    }

    internal static object DescribeMessage(StoredMessage message)
    {
        return new
        {
            id = message.Id,
            index = message.Index,
            role = message.Role,
            text = TruncateForDisplay(message.Text),
            truncated = message.Text.Length > TruncatePreviewLimit,
            turnId = message.TurnId,
            toolCallId = message.ToolCallId,
            name = message.Name,
            toolCalls = message.ToolCalls,
            images = message.Images,
            inputTokens = message.InputTokens,
            outputTokens = message.OutputTokens,
            cachedTokens = message.CachedTokens,
            reasoningContent = message.ReasoningContent,
            validatedWarnings = message.ValidatedWarnings,
            errorCode = message.ErrorCode,
            createdAtUtc = message.CreatedAt ?? message.StoredAt,
        };
    }

    internal static string? ReadOptionalString(JsonElement payload, string name)
    {
        if (
            payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }
        return node.GetString();
    }

    internal static ResponseFrame RunEchoTurn(
        RequestFrame request,
        SessionLedger sessions,
        TurnCoordinator turns,
        UsageLedger usage,
        IEventJournal events,
        AttachmentIndex attachments,
        string turnId,
        string sessionId,
        string text
    )
    {
        bool needsConfirm = TryReadBool(request.Payload, "needsConfirm");
        sessions.Append(sessionId, "user", text, turnId);
        events.Append("turn.started", new { turnId, sessionId });
        events.Append("text.delta", new { turnId, delta = text });
        events.Append("text.done", new { turnId, length = text.Length });
        usage.Record(text.Length / 4, text.Length / 4);
        events.Append(
            "usage.tick",
            new
            {
                turnId,
                inputTokens = text.Length / 4,
                outputTokens = text.Length / 4,
            }
        );
        if (needsConfirm)
        {
            string toolCallId = Guid.NewGuid().ToString("N");
            turns.RaiseConfirmation(turnId, toolCallId, "edit.apply", request.Payload);
            events.Append(
                "tool.confirm",
                new
                {
                    turnId,
                    toolCallId,
                    toolName = "edit.apply"
                }
            );
            sessions.Append(sessionId, "assistant", text, turnId);
            return ResponseFrame.Success(request.Id, new { turnId, awaitingConfirmation = true });
        }
        var attached = RegisterAttachments(request.Payload, sessionId, attachments);
        string toolCall = Guid.NewGuid().ToString("N");
        events.Append(
            "tool.start",
            new
            {
                turnId,
                toolCallId = toolCall,
                toolName = "reply",
                attachments = attached.Select(a => new
                {
                    digest = a.Digest,
                    fileName = a.FileName,
                    byteSize = a.ByteSize,
                    path = a.RelativePath
                })
            }
        );
        events.Append(
            "tool.delta",
            new
            {
                turnId,
                toolCallId = toolCall,
                delta = text
            }
        );
        events.Append(
            "tool.end",
            new
            {
                turnId,
                toolCallId = toolCall,
                ok = true
            }
        );
        sessions.Append(sessionId, "assistant", text, turnId);
        events.Append("turn.done", new { turnId, sessionId });
        turns.Finish(turnId, TurnOutcome.Done);
        return ResponseFrame.Success(request.Id, new { turnId });
    }

    internal static string[] ReadEnabledBatches()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable("HALO_ENGINE_ROUTING");
        }
        catch
        {
            return Array.Empty<string>();
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }
        string normalized = raw.Trim().ToUpperInvariant();
        if (normalized == "ALL")
        {
            return new[] { "A", "B", "C" };
        }
        return normalized
            .Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p == "A" || p == "B" || p == "C")
            .Distinct()
            .ToArray();
    }

    private static string? ReadEntryString(JsonElement entry, string name)
    {
        return entry.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static long ReadEntrySize(JsonElement entry)
    {
        if (
            entry.TryGetProperty("sizeBytes", out var sized)
            && sized.ValueKind == JsonValueKind.Number
            && sized.TryGetInt64(out long sizedValue)
        )
        {
            return sizedValue;
        }
        if (
            entry.TryGetProperty("byteSize", out var legacy)
            && legacy.ValueKind == JsonValueKind.Number
            && legacy.TryGetInt64(out long legacyValue)
        )
        {
            return legacyValue;
        }
        return 0;
    }

    private static bool EnvSwitchOff(string name)
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }
            string value = raw.Trim();
            return string.Equals(value, "0", StringComparison.Ordinal)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string TrimHint(string? hint)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return string.Empty;
        }
        return hint.Length > 300 ? hint.Substring(0, 300) : hint;
    }

    private static readonly SemaphoreSlim SmallRequestPool = new(2, 2);

    private static async Task<string> BuildCompactionSummaryAsync(
        SessionLedger sessions,
        IEngineContext context,
        string sessionId,
        IReadOnlyList<Abstractions.StoredMessage> rows,
        int keepGroups,
        CancellationToken token
    )
    {
        int timeoutSec = context.TurnOptions?.CompactTimeoutSec ?? 20;
        if (timeoutSec <= 0)
        {
            timeoutSec = 20;
        }
        using var timeoutScope = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 120))
        );
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            timeoutScope.Token,
            sessions.CompactionToken(sessionId)
        );
        bool entered = false;
        try
        {
            entered = await SmallRequestPool
                .WaitAsync(TimeSpan.FromSeconds(5), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Compaction summary pool wait timed out.");
        }
        if (!entered)
        {
            throw new TimeoutException("Compaction summary pool is busy.");
        }
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (context.Gateway is null)
            {
                return BuildEstimatedSummary(rows, keepGroups);
            }
            var body = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var row in rows)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (shown >= 40)
                {
                    break;
                }
                string text = row.Text ?? string.Empty;
                if (text.Length > 2000)
                {
                    text = text.Substring(0, 2000);
                }
                body.Append('[').Append(row.Role).Append("] ").AppendLine(text);
                shown++;
            }
            string workspace = SessionIdentity.DefaultWorkspace;
            try
            {
                if (
                    sessions.TryGetDescriptor(sessionId, out var descriptor)
                    && descriptor is not null
                )
                {
                    workspace = string.IsNullOrWhiteSpace(descriptor.WorkspaceId)
                        ? SessionIdentity.DefaultWorkspace
                        : descriptor.WorkspaceId;
                }
            }
            catch { }
            var gatewayRequest = new Abstractions.EngineLlmRequest(
                sessionId,
                "compact:" + sessionId,
                workspace,
                "Summarize the conversation into a concise Chinese bullet list. Keep task goal, completed actions with paths and results, user decisions, and pending work. Output only bullets.",
                Array.Empty<Abstractions.EngineHistoryMessage>(),
                null,
                null,
                1
            );
            var summaryAcc = new System.Text.StringBuilder();
            bool sawError = false;
            string errorCode = string.Empty;
            await foreach (
                var evt in context
                    .Gateway.StreamAsync(gatewayRequest, linked.Token)
                    .ConfigureAwait(false)
            )
            {
                if (
                    evt.Kind == Abstractions.EngineLlmEventKind.TextDelta
                    && !string.IsNullOrEmpty(evt.TextDelta)
                )
                {
                    summaryAcc.Append(evt.TextDelta);
                    if (summaryAcc.Length > 2000)
                    {
                        break;
                    }
                }
                else if (evt.Kind == Abstractions.EngineLlmEventKind.Error)
                {
                    sawError = true;
                    errorCode = evt.ErrorCode ?? string.Empty;
                    break;
                }
            }
            string summary = summaryAcc.ToString().Trim();
            if (sawError || string.IsNullOrWhiteSpace(summary))
            {
                if (
                    sawError
                    && (
                        errorCode.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
                        || errorCode.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    throw new InvalidOperationException("NO_API_KEY");
                }
                return BuildEstimatedSummary(rows, keepGroups);
            }
            if (summary.Length > 2000)
            {
                summary = summary.Substring(0, 2000);
            }
            return summary;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Compaction summary timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            try
            {
                SmallRequestPool.Release();
            }
            catch { }
        }
    }

    private static string BuildEstimatedSummary(
        IReadOnlyList<Abstractions.StoredMessage> rows,
        int keepGroups
    )
    {
        int users = rows.Count(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
        );
        int tools = rows.Count(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
        );
        var tailTitles = rows.TakeLast(Math.Min(3, rows.Count))
            .Select(m => (m.Text ?? string.Empty).Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t.Length > 60 ? t.Substring(0, 60) : t)
            .ToList();
        var builder = new System.Text.StringBuilder();
        builder.Append(
            $"Context summary [estimated]: {rows.Count} messages, {users} user turns, {tools} tool results; kept last {keepGroups} groups. "
        );
        if (tailTitles.Count > 0)
        {
            builder.Append("Recent: ");
            builder.Append(string.Join(" | ", tailTitles));
        }
        string result = builder.ToString().Trim();
        if (result.Length > 2000)
        {
            result = result.Substring(0, 2000);
        }
        return result;
    }
}
