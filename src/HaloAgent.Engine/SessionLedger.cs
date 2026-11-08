using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Session truth owned by the engine: titles, history, compaction guard.
/// Inner repository lock; always acquired inside the frame write gate, never the reverse.
/// History paging uses an index cursor so the display layer can lazily fetch windows.
/// </summary>
public sealed class SessionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StoredMessage>> _history = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compacting = new(StringComparer.Ordinal);
    private long _messageIndex;

    public bool EnsureSession(string sessionId, string title, string workspaceId = "default")
    {
        lock (_gate)
        {
            _titles[sessionId] = title;
            _workspaces[sessionId] = string.IsNullOrWhiteSpace(workspaceId)
                ? "default"
                : workspaceId;
            _history.TryAdd(sessionId, new List<StoredMessage>());
            return true;
        }
    }

    public bool TryGetTitle(string sessionId, out string title)
    {
        lock (_gate)
        {
            return _titles.TryGetValue(sessionId, out title!);
        }
    }

    public bool Rename(string sessionId, string title)
    {
        lock (_gate)
        {
            if (!_titles.ContainsKey(sessionId))
            {
                return false;
            }
            _titles[sessionId] = title;
            return true;
        }
    }

    public bool Delete(string sessionId)
    {
        lock (_gate)
        {
            _titles.Remove(sessionId);
            _workspaces.Remove(sessionId);
            _history.Remove(sessionId);
            _compacting.Remove(sessionId);
            return true;
        }
    }

    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            if (_history.TryGetValue(sessionId, out var list))
            {
                list.Clear();
            }
        }
    }

    public IReadOnlyList<(string SessionId, string Title)> List()
    {
        lock (_gate)
        {
            return _titles.Select(e => (e.Key, e.Value)).ToList();
        }
    }

    public StoredMessage Append(string sessionId, string role, string text, string turnId)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(sessionId, out var list))
            {
                list = new List<StoredMessage>();
                _history[sessionId] = list;
                _titles.TryAdd(sessionId, "New session");
            }
            _messageIndex += 1;
            var stored = new StoredMessage(
                _messageIndex,
                role,
                text,
                turnId,
                DateTimeOffset.UtcNow
            );
            list.Add(stored);
            return stored;
        }
    }

    public (IReadOnlyList<StoredMessage> Items, string NextCursor, bool HasMore) ReadPage(
        string sessionId,
        string cursor,
        int limit
    )
    {
        int take = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        lock (_gate)
        {
            if (!_history.TryGetValue(sessionId, out var list) || list.Count == 0)
            {
                return (Array.Empty<StoredMessage>(), "0", false);
            }
            long since = 0;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                long.TryParse(cursor, out since);
            }
            var window = list.Where(m => m.Index > since).Take(take).ToList();
            string next = window.Count > 0 ? window[^1].Index.ToString() : since.ToString();
            bool hasMore = window.Count > 0 && window[^1].Index < list[^1].Index;
            return (window, next, hasMore);
        }
    }

    public int Count(string sessionId)
    {
        lock (_gate)
        {
            return _history.TryGetValue(sessionId, out var list) ? list.Count : 0;
        }
    }

    public bool TryBeginCompaction(string sessionId)
    {
        lock (_gate)
        {
            if (!_titles.ContainsKey(sessionId) || _compacting.Contains(sessionId))
            {
                return false;
            }
            _compacting.Add(sessionId);
            return true;
        }
    }

    public void EndCompaction(string sessionId, string summary)
    {
        lock (_gate)
        {
            _compacting.Remove(sessionId);
            if (_history.TryGetValue(sessionId, out var list))
            {
                list.Clear();
                _messageIndex += 1;
                list.Add(
                    new StoredMessage(
                        _messageIndex,
                        "system",
                        summary,
                        string.Empty,
                        DateTimeOffset.UtcNow
                    )
                );
            }
        }
    }

    public bool IsCompacting(string sessionId)
    {
        lock (_gate)
        {
            return _compacting.Contains(sessionId);
        }
    }

    public IReadOnlyList<StoredMessage> Snapshot(string sessionId)
    {
        lock (_gate)
        {
            return _history.TryGetValue(sessionId, out var list)
                ? list.ToList()
                : new List<StoredMessage>();
        }
    }

    public void Restore(string sessionId, string title, IEnumerable<StoredMessage> messages)
    {
        lock (_gate)
        {
            _titles[sessionId] = title;
            var list = new List<StoredMessage>(messages);
            _history[sessionId] = list;
            foreach (var item in list)
            {
                if (item.Index > _messageIndex)
                {
                    _messageIndex = item.Index;
                }
            }
        }
    }
}
