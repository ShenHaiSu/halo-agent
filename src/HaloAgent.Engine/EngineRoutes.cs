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
        IEventJournal events
    )
    {
        routes["system.ping"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { pong = true }));

        routes["system.info"] = (request, context, token) =>
            Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        version = ContractCatalog.ContractVersion,
                        queueBound = FrameServer.QueueBound,
                        journalCapacity = events.Capacity,
                    }
                )
            );

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
            string sessionId = Guid.NewGuid().ToString("N");
            sessions.EnsureSession(
                sessionId,
                string.IsNullOrWhiteSpace(title) ? "New session" : title
            );
            state.TryRememberSession(
                sessionId,
                string.IsNullOrWhiteSpace(title) ? "New session" : title
            );
            state.ActiveSessionId = sessionId;
            events.Append("session.updated", new { sessionId, reason = "created" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId }));
        };

        routes["session.list"] = (request, context, token) =>
        {
            var items = sessions
                .List()
                .Select(e => new { sessionId = e.SessionId, title = e.Title })
                .ToList();
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessions = items }));
        };

        routes["session.get"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (!sessions.TryGetTitle(sessionId, out string? title))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        title,
                        messageCount = sessions.Count(sessionId),
                    }
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

        routes["session.delete"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            sessions.Delete(sessionId);
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
            var page = sessions.ReadPage(sessionId, cursor, limit);
            var items = page
                .Items.Select(m => new
                {
                    index = m.Index,
                    role = m.Role,
                    text = TruncateForDisplay(m.Text),
                    truncated = m.Text.Length > TruncatePreviewLimit,
                    turnId = m.TurnId,
                })
                .ToList();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId,
                        items,
                        nextCursor = page.NextCursor,
                        hasMore = page.HasMore,
                    }
                )
            );
        };

        routes["session.compact"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "sessionId is required.")
                );
            }
            if (!sessions.TryBeginCompaction(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "COMPACTING", "Compaction is already running.")
                );
            }
            string summary = $"Compacted {sessions.Count(sessionId)} messages.";
            sessions.EndCompaction(sessionId, summary);
            events.Append("session.compacted", new { sessionId, summary });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId, summary }));
        };

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
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "sessionId and text are required."
                    )
                );
            }
            if (!sessions.TryGetTitle(sessionId, out _))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
            if (sessions.IsCompacting(sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "COMPACTING",
                        "Wait for compaction or stop it first."
                    )
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
                return Task.FromResult(
                    ResponseFrame.Success(request.Id, new { turnId, awaitingConfirmation = true })
                );
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
            if (!turns.TryTakeConfirmation(turnId, out _))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "NOT_FOUND",
                        "No pending confirmation for this turn."
                    )
                );
            }
            turns.ResolveConfirmation(turnId, approved);
            if (!turns.TrySessionOf(turnId, out string? sessionId))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Turn is unknown.")
                );
            }
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
            turns.TryStart(sessionId, turnId, request.Id, out _);
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
            return Task.FromResult(ResponseFrame.Success(request.Id, new { turnId }));
        };

        routes["agent.queue"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { queued = true }));
        routes["agent.drain"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { drained = true }));
        routes["agent.unqueue"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { unqueued = true }));

        routes["tool.undoRollback"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            if (!sessions.TryRollbackLast(sessionId, out var removed))
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
                    removedTurnId = removed!.TurnId
                }
            );
            return Task.FromResult(
                ResponseFrame.Success(request.Id, new { sessionId, rolledBack = true })
            );
        };

        routes["usage.query"] = (request, context, token) =>
        {
            var current = usage.Query();
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        inputTokens = current.InputTokens,
                        outputTokens = current.OutputTokens,
                        cachedTokens = current.CachedTokens,
                        retryCount = current.RetryCount,
                    }
                )
            );
        };

        routes["usage.clear"] = (request, context, token) =>
        {
            usage.Clear();
            return Task.FromResult(ResponseFrame.Success(request.Id, new { cleared = true }));
        };

        foreach (
            string name in new[]
            {
                "settings.saveProviders",
                "settings.setActive",
                "settings.saveNetwork",
                "settings.saveBehavior",
                "asset.skills.list",
                "asset.skills.get",
                "asset.memory.list",
                "asset.memory.save",
                "asset.personas.list",
                "asset.personas.get",
            }
        )
        {
            string captured = name;
            if (routes.ContainsKey(captured))
            {
                continue;
            }
            routes[captured] = (request, context, token) =>
                Task.FromResult(
                    ResponseFrame.Success(request.Id, new { saved = true, method = captured })
                );
        }

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
            string digest = entry.TryGetProperty("digest", out var digestNode)
                ? digestNode.GetString() ?? string.Empty
                : string.Empty;
            string fileName = entry.TryGetProperty("fileName", out var nameNode)
                ? nameNode.GetString() ?? "attachment.bin"
                : "attachment.bin";
            long byteSize =
                entry.TryGetProperty("byteSize", out var sizeNode)
                && sizeNode.TryGetInt64(out long parsed)
                    ? parsed
                    : 0;
            if (string.IsNullOrWhiteSpace(digest))
            {
                continue;
            }
            registered.Add(attachments.Register(digest, sessionId, fileName, byteSize));
        }
        return registered;
    }
}
