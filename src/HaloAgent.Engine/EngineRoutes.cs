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
                        contractVersion = ContractCatalog.ContractVersion,
                        queueBound = FrameServer.QueueBound,
                        journalCapacity = events.Capacity,
                        routing = new
                        {
                            enabledBatches = ReadEnabledBatches(),
                            defaultFrame = false,
                        },
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
            if (!sessions.TryGetDescriptor(sessionId, out var descriptor) || descriptor is null)
            {
                return Task.FromResult(
                    ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.")
                );
            }
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
            sessions.Rename(sessionId, title);
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
}
