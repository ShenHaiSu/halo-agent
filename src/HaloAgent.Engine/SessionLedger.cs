using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Session truth owned by the engine: titles, history, compaction guard.
/// Inner repository lock; always acquired inside the frame write gate, never the reverse.
/// History paging uses an index cursor so the display layer can lazily fetch windows;
/// message ids are accepted as cursors too for parity with the desktop store.
/// Paging cursors are per session so parallel sessions never scramble each other.
/// </summary>
public sealed class SessionLedger
{
    public const int PreviewTextLimit = 2048;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionDescriptor> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StoredMessage>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compacting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _compactTokens =
        new(StringComparer.Ordinal);
    private readonly List<string> _workspaceFallbackAudit = new();

    public bool EnsureSession(string sessionId, string title, string workspaceId = "default")
    {
        return EnsureSession(
            sessionId,
            title,
            workspaceId,
            parentSessionId: null,
            providerId: null,
            modelId: null
        );
    }

    public bool EnsureSession(
        string sessionId,
        string title,
        string? workspaceId,
        string? parentSessionId,
        string? providerId,
        string? modelId,
        string agentRole = "main",
        bool isPinned = false,
        bool titleLocked = false
    )
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        string workspace = ResolveWorkspace(workspaceId);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                _sessions[sessionId] = existing with
                {
                    Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title,
                    WorkspaceId = workspace,
                    ParentSessionId = parentSessionId ?? existing.ParentSessionId,
                    ProviderId = providerId ?? existing.ProviderId,
                    ModelId = modelId ?? existing.ModelId,
                    AgentRole = string.IsNullOrWhiteSpace(agentRole)
                        ? existing.AgentRole
                        : agentRole,
                    IsPinned = isPinned || existing.IsPinned,
                    TitleLocked = titleLocked || existing.TitleLocked,
                    UpdatedAtUtc = now,
                };
            }
            else
            {
                _sessions[sessionId] = new SessionDescriptor(
                    sessionId,
                    string.IsNullOrWhiteSpace(title) ? "New session" : title,
                    workspace,
                    parentSessionId,
                    providerId,
                    modelId,
                    string.IsNullOrWhiteSpace(agentRole) ? "main" : agentRole,
                    isPinned,
                    titleLocked,
                    now,
                    now
                );
            }
            _history.TryAdd(sessionId, new List<StoredMessage>());
            _nextIndex.TryAdd(sessionId, 0);
            return true;
        }
    }

    public bool TryGetTitle(string sessionId, out string title)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(ResolveAlias(sessionId), out var descriptor))
            {
                title = descriptor.Title;
                return true;
            }
            title = string.Empty;
            return false;
        }
    }

    public bool TryGetDescriptor(string sessionId, out SessionDescriptor? descriptor)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(ResolveAlias(sessionId), out descriptor);
        }
    }

    public string ResolveWorkspace(string? workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            return workspaceId;
        }
        lock (_gate)
        {
            _workspaceFallbackAudit.Add(
                $"{DateTimeOffset.UtcNow:O} workspace missing, routed to '{SessionIdentity.DefaultWorkspace}'."
            );
        }
        return SessionIdentity.DefaultWorkspace;
    }

    public int WorkspaceFallbackCount
    {
        get
        {
            lock (_gate)
            {
                return _workspaceFallbackAudit.Count;
            }
        }
    }

    public IReadOnlyList<string> WorkspaceFallbackAudit
    {
        get
        {
            lock (_gate)
            {
                return _workspaceFallbackAudit.ToList();
            }
        }
    }

    public bool Rename(string sessionId, string title)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            if (descriptor.TitleLocked)
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                Title = title,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public bool SetProvider(string sessionId, string? providerId, string? modelId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                ProviderId = providerId ?? descriptor.ProviderId,
                ModelId = modelId ?? descriptor.ModelId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }
    }

    public bool SetTitleLocked(string sessionId, bool titleLocked)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                TitleLocked = titleLocked,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public bool SetPinned(string sessionId, bool isPinned)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                IsPinned = isPinned,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public bool SetAgentRole(string sessionId, string agentRole)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                AgentRole = string.IsNullOrWhiteSpace(agentRole) ? descriptor.AgentRole : agentRole,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }
    }

    public bool SetParent(string sessionId, string? parentSessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with
            {
                ParentSessionId = parentSessionId ?? descriptor.ParentSessionId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }
    }

    public bool Touch(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_sessions.TryGetValue(key, out var descriptor))
            {
                return false;
            }
            _sessions[key] = descriptor with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            return true;
        }
    }

    public bool Delete(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            bool removed = _sessions.Remove(key);
            _history.Remove(key);
            _nextIndex.Remove(key);
            _compacting.Remove(key);
            var stale = _aliases
                .Where(e => string.Equals(e.Value, key, StringComparison.Ordinal))
                .Select(e => e.Key)
                .ToList();
            foreach (string alias in stale)
            {
                _aliases.Remove(alias);
            }
            return removed;
        }
    }

    /// <summary>All known session ids with titles, default scope only (startup sweep helper).</summary>
    public IReadOnlyList<(string SessionId, string Title)> ListPairs()
    {
        return List(SessionIdentity.DefaultWorkspace).Select(d => (d.SessionId, d.Title)).ToList();
    }

    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (_history.TryGetValue(key, out var list))
            {
                list.Clear();
            }
            if (_sessions.TryGetValue(key, out var descriptor))
            {
                _sessions[key] = descriptor with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    public bool TryRollbackLast(string sessionId, out StoredMessage? removed)
    {
        return TryRollbackTurn(sessionId, out _, out removed);
    }

    /// <summary>
    /// Rolls back the newest turn as a whole: every message that carries the
    /// newest turn id is removed together, matching desktop rewrite semantics.
    /// Single-message callers keep working through <see cref="TryRollbackLast"/>.
    /// </summary>
    public bool TryRollbackTurn(
        string sessionId,
        out IReadOnlyList<StoredMessage> removedMessages,
        out StoredMessage? lastRemoved
    )
    {
        lock (_gate)
        {
            removedMessages = Array.Empty<StoredMessage>();
            lastRemoved = null;
            string key = ResolveAlias(sessionId);
            if (!_history.TryGetValue(key, out var list) || list.Count == 0)
            {
                return false;
            }
            string newestTurn = list[^1].TurnId;
            var removed = string.IsNullOrWhiteSpace(newestTurn)
                ? new List<StoredMessage> { list[^1] }
                : list.Where(m => string.Equals(m.TurnId, newestTurn, StringComparison.Ordinal))
                    .ToList();
            foreach (var item in removed)
            {
                list.Remove(item);
            }
            lastRemoved = removed.Count > 0 ? removed[^1] : null;
            removedMessages = removed;
            if (_sessions.TryGetValue(key, out var descriptor))
            {
                _sessions[key] = descriptor with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            }
            return removed.Count > 0;
        }
    }

    /// <summary>
    /// Workspace-scoped listing ordered by most recently updated first.
    /// A missing workspace falls back to the default scope and is audited,
    /// so reads never silently sweep every workspace.
    /// </summary>
    public IReadOnlyList<SessionDescriptor> List(string? workspaceId = null)
    {
        string workspace = ResolveWorkspace(workspaceId);
        lock (_gate)
        {
            return _sessions
                .Values.Where(d =>
                    string.Equals(d.WorkspaceId, workspace, StringComparison.Ordinal)
                )
                .OrderByDescending(d => d.UpdatedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(d => d.SessionId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public StoredMessage Append(string sessionId, string role, string text, string turnId)
    {
        return Append(sessionId, role, text, turnId, messageId: null);
    }

    public StoredMessage Append(
        string sessionId,
        string role,
        string text,
        string turnId,
        string? messageId,
        string? toolCallId = null,
        string? name = null,
        IReadOnlyList<SessionToolCall>? toolCalls = null,
        IReadOnlyList<SessionImageReference>? images = null,
        long? inputTokens = null,
        long? outputTokens = null,
        long? cachedTokens = null,
        string? reasoningContent = null,
        string? validatedWarnings = null,
        string? errorCode = null
    )
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_history.TryGetValue(key, out var list))
            {
                list = new List<StoredMessage>();
                _history[key] = list;
                _sessions.TryAdd(
                    key,
                    new SessionDescriptor(key, "New session", SessionIdentity.DefaultWorkspace)
                    {
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    }
                );
                _nextIndex.TryAdd(key, 0);
            }
            long index = (_nextIndex.TryGetValue(key, out long next) ? next : 0) + 1;
            _nextIndex[key] = index;
            var now = DateTimeOffset.UtcNow;
            var stored = new StoredMessage(
                index,
                role,
                text,
                turnId,
                now,
                string.IsNullOrWhiteSpace(messageId) ? SessionIdentity.NewMessageId() : messageId,
                toolCallId,
                name,
                toolCalls,
                images,
                inputTokens,
                outputTokens,
                cachedTokens,
                reasoningContent,
                validatedWarnings,
                errorCode,
                now
            );
            list.Add(stored);
            if (_sessions.TryGetValue(key, out var descriptor))
            {
                _sessions[key] = descriptor with { UpdatedAtUtc = now };
            }
            return stored;
        }
    }

    public (IReadOnlyList<StoredMessage> Items, string NextCursor, bool HasMore) ReadPage(
        string sessionId,
        string cursor,
        int limit
    )
    {
        var page = ReadPage(sessionId, cursor, limit, out _);
        return page;
    }

    public (IReadOnlyList<StoredMessage> Items, string NextCursor, bool HasMore) ReadPage(
        string sessionId,
        string cursor,
        int limit,
        out long total
    )
    {
        int take = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            if (!_history.TryGetValue(key, out var list) || list.Count == 0)
            {
                total = 0;
                return (Array.Empty<StoredMessage>(), "0", false);
            }
            total = list.Count;
            long since = ResolveCursor(list, cursor);
            var window = list.Where(m => m.Index > since).Take(take).ToList();
            string next = window.Count > 0 ? window[^1].Index.ToString() : since.ToString();
            bool hasMore = window.Count > 0 && window[^1].Index < list[^1].Index;
            return (window, next, hasMore);
        }
    }

    private static long ResolveCursor(IReadOnlyList<StoredMessage> list, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }
        string trimmed = cursor.Trim();
        var byId = list.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.Ordinal));
        if (byId is not null)
        {
            return byId.Index;
        }
        return long.TryParse(trimmed, out long since) ? since : 0;
    }

    public int Count(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            return _history.TryGetValue(key, out var list) ? list.Count : 0;
        }
    }

    public long Total(string sessionId) => Count(sessionId);

    public bool TryBeginCompaction(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAliasLocked(sessionId);
            if (
                string.IsNullOrWhiteSpace(key)
                || !_sessions.ContainsKey(key)
                || _compacting.Contains(key)
            )
            {
                return false;
            }
            _compacting.Add(key);
            try
            {
                if (_compactTokens.TryGetValue(key, out var previous))
                {
                    try
                    {
                        previous.Dispose();
                    }
                    catch { }
                }
                _compactTokens[key] = new CancellationTokenSource();
            }
            catch { }
            return true;
        }
    }

    public CancellationToken CompactionToken(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAliasLocked(sessionId);
            if (!string.IsNullOrEmpty(key) && _compactTokens.TryGetValue(key, out var scope))
            {
                return scope.Token;
            }
            return CancellationToken.None;
        }
    }

    public bool TryCancelCompaction(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAliasLocked(sessionId);
            if (string.IsNullOrEmpty(key) || !_compacting.Contains(key))
            {
                return false;
            }
            if (_compactTokens.TryGetValue(key, out var scope))
            {
                try
                {
                    scope.Cancel();
                }
                catch { }
            }
            _compacting.Remove(key);
            if (_compactTokens.TryGetValue(key, out var owned))
            {
                _compactTokens.Remove(key);
                try
                {
                    owned.Dispose();
                }
                catch { }
            }
            return true;
        }
    }

    public void EndCompaction(string sessionId, string summary)
    {
        lock (_gate)
        {
            string key = ResolveAliasLocked(sessionId);
            _compacting.Remove(key);
            if (_compactTokens.TryGetValue(key, out var owned))
            {
                _compactTokens.Remove(key);
                try
                {
                    owned.Dispose();
                }
                catch { }
            }
            if (_history.TryGetValue(key, out var list))
            {
                list.Clear();
                long index = (_nextIndex.TryGetValue(key, out long next) ? next : 0) + 1;
                _nextIndex[key] = index;
                var now = DateTimeOffset.UtcNow;
                list.Add(
                    new StoredMessage(
                        index,
                        "system",
                        summary,
                        string.Empty,
                        now,
                        SessionIdentity.NewMessageId(),
                        CreatedAt: now
                    )
                );
            }
            if (_sessions.TryGetValue(key, out var descriptor))
            {
                _sessions[key] = descriptor with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    public bool IsCompacting(string sessionId)
    {
        lock (_gate)
        {
            return _compacting.Contains(ResolveAliasLocked(sessionId));
        }
    }

    public IReadOnlyList<string> ListTreeSessions(string rootSessionId)
    {
        lock (_gate)
        {
            string root = ResolveAliasLocked(rootSessionId);
            var found = new List<string>();
            if (string.IsNullOrEmpty(root) || !_sessions.ContainsKey(root))
            {
                return found;
            }
            found.Add(root);
            bool grown = true;
            while (grown)
            {
                grown = false;
                foreach (var entry in _sessions)
                {
                    if (found.Contains(entry.Key, StringComparer.Ordinal))
                    {
                        continue;
                    }
                    string? parent = entry.Value.ParentSessionId;
                    if (
                        !string.IsNullOrWhiteSpace(parent)
                        && found.Contains(ResolveAliasLocked(parent), StringComparer.Ordinal)
                    )
                    {
                        found.Add(entry.Key);
                        grown = true;
                    }
                }
            }
            return found;
        }
    }

    public bool TryCompactWithSummary(
        string sessionId,
        string summary,
        int keepLastGroups,
        out int removed,
        out int kept
    )
    {
        lock (_gate)
        {
            removed = 0;
            kept = 0;
            string key = ResolveAliasLocked(sessionId);
            if (
                string.IsNullOrEmpty(key)
                || !_history.TryGetValue(key, out var list)
                || list.Count == 0
            )
            {
                return false;
            }
            int keep = keepLastGroups <= 0 ? 6 : Math.Clamp(keepLastGroups, 1, 32);
            var groups = GroupByTurn(list);
            if (groups.Count <= keep + 1)
            {
                return false;
            }
            int tailStart = Math.Max(1, groups.Count - keep);
            var anchor = groups.Take(1).SelectMany(g => g).ToList();
            var tail = groups.Skip(tailStart).SelectMany(g => g).ToList();
            var middle = groups.Skip(1).Take(tailStart - 1).SelectMany(g => g).ToList();
            if (middle.Count == 0)
            {
                return false;
            }
            removed = middle.Count;
            var rewritten = new List<StoredMessage>(anchor.Count + tail.Count + 1);
            rewritten.AddRange(anchor);
            long index = (_nextIndex.TryGetValue(key, out long next) ? next : list.Count) + 1;
            _nextIndex[key] = index;
            var now = DateTimeOffset.UtcNow;
            rewritten.Add(
                new StoredMessage(
                    index,
                    "system",
                    summary,
                    string.Empty,
                    now,
                    SessionIdentity.NewMessageId(),
                    CreatedAt: now
                )
            );
            rewritten.AddRange(tail);
            _history[key] = rewritten;
            kept = rewritten.Count;
            if (_sessions.TryGetValue(key, out var descriptor))
            {
                _sessions[key] = descriptor with { UpdatedAtUtc = now };
            }
            return true;
        }
    }

    private static IReadOnlyList<IReadOnlyList<StoredMessage>> GroupByTurn(
        IReadOnlyList<StoredMessage> rows
    )
    {
        var groups = new List<List<StoredMessage>>();
        foreach (var row in rows)
        {
            string turn = row.TurnId ?? string.Empty;
            if (groups.Count > 0)
            {
                string headTurn =
                    groups[^1].Count > 0 ? groups[^1][0].TurnId ?? string.Empty : string.Empty;
                bool sameTurn =
                    !string.IsNullOrEmpty(turn)
                    && string.Equals(turn, headTurn, StringComparison.Ordinal);
                bool bothLoose = string.IsNullOrEmpty(turn) && string.IsNullOrEmpty(headTurn);
                if (sameTurn || bothLoose)
                {
                    groups[^1].Add(row);
                    continue;
                }
            }
            groups.Add(new List<StoredMessage> { row });
        }
        return groups;
    }

    public IReadOnlyList<StoredMessage> Snapshot(string sessionId)
    {
        lock (_gate)
        {
            string key = ResolveAlias(sessionId);
            return _history.TryGetValue(key, out var list)
                ? list.ToList()
                : new List<StoredMessage>();
        }
    }

    public void Restore(string sessionId, string title, IEnumerable<StoredMessage> messages)
    {
        Restore(
            sessionId,
            new SessionDescriptor(sessionId, title, SessionIdentity.DefaultWorkspace),
            messages
        );
    }

    public void Restore(
        string sessionId,
        SessionDescriptor descriptor,
        IEnumerable<StoredMessage> messages
    )
    {
        lock (_gate)
        {
            var list = new List<StoredMessage>(messages);
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    list[list.IndexOf(item)] = item with { Id = SessionIdentity.NewMessageId() };
                }
            }
            _sessions[sessionId] = descriptor with { SessionId = sessionId };
            _history[sessionId] = list;
            long high = 0;
            foreach (var item in list)
            {
                if (item.Index > high)
                {
                    high = item.Index;
                }
            }
            _nextIndex[sessionId] = high;
        }
    }

    public void RegisterAlias(string previousId, string currentId)
    {
        if (string.IsNullOrWhiteSpace(previousId) || string.IsNullOrWhiteSpace(currentId))
        {
            return;
        }
        lock (_gate)
        {
            _aliases[previousId] = currentId;
        }
    }

    public string ResolveAlias(string sessionId)
    {
        lock (_gate)
        {
            return ResolveAliasLocked(sessionId);
        }
    }

    private string ResolveAliasLocked(string sessionId)
    {
        string current = sessionId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (
            !string.IsNullOrEmpty(current)
            && _aliases.TryGetValue(current, out string? next)
            && seen.Add(current)
        )
        {
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Startup healing: links previous-generation ids to directory-shaped ids
    /// and links every stored row back to a descriptor so reloads never drop.
    /// </summary>
    public int HealAliases()
    {
        lock (_gate)
        {
            int healed = 0;
            foreach (var id in _history.Keys.ToList())
            {
                if (
                    !_sessions.ContainsKey(id)
                    && ResolveAliasLocked(id) != id
                    && _sessions.ContainsKey(ResolveAliasLocked(id))
                )
                {
                    healed++;
                }
            }
            return healed;
        }
    }
}
