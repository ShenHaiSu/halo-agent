using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

internal sealed class SessionDeletionTreeResult
{
    public bool RootMissing { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<SessionDeletionNode> Nodes { get; init; } =
        Array.Empty<SessionDeletionNode>();
}

internal static class SessionDeletionTree
{
    public static SessionDeletionTreeResult Collect(
        string rootSessionId,
        IReadOnlyList<SessionMetadata> sessions
    )
    {
        var byId = new Dictionary<string, SessionMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionMetadata session in sessions)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                continue;
            string id = session.Id.Trim();
            if (!byId.ContainsKey(id))
                byId.Add(id, session);
        }

        if (!byId.TryGetValue(rootSessionId, out SessionMetadata? rootMetadata))
            return new SessionDeletionTreeResult { RootMissing = true };
        string canonicalRootId = rootMetadata.Id.Trim();

        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionMetadata session in byId.Values)
        {
            if (string.IsNullOrWhiteSpace(session.ParentSessionId))
                continue;
            string parentId = session.ParentSessionId.Trim();
            if (!byId.ContainsKey(parentId))
                continue;
            if (!children.TryGetValue(parentId, out var list))
            {
                list = new List<string>();
                children.Add(parentId, list);
            }
            list.Add(session.Id.Trim());
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<SessionDeletionNode>();
        var pending = new Queue<(string Id, int Depth)>();
        pending.Enqueue((canonicalRootId, 0));
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current.Id))
                return new SessionDeletionTreeResult { ErrorCode = "SESSION_TREE_CYCLE" };
            nodes.Add(new SessionDeletionNode { SessionId = current.Id, Depth = current.Depth, });
            if (!children.TryGetValue(current.Id, out var childIds))
                continue;
            foreach (
                string childId in childIds
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(id => id, StringComparer.Ordinal)
            )
                pending.Enqueue((childId, current.Depth + 1));
        }

        return new SessionDeletionTreeResult
        {
            Nodes = nodes
                .OrderBy(node => node.SessionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.SessionId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public static IEnumerable<SessionDeletionNode> OrderDirectoriesDeepFirst(
        IEnumerable<SessionDeletionNode> nodes
    ) =>
        nodes
            .OrderByDescending(node => node.Depth)
            .ThenBy(node => node.SessionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.SessionId, StringComparer.Ordinal);
}
