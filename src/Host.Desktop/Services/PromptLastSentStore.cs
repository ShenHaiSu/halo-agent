using System.Collections.Concurrent;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>need06-03 §2.3：上轮实际发送快照（内存，最近1条/会话，不落盘）。</summary>
public sealed class LastSentPromptSnapshot
{
    public string ReqId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string PrefixHash { get; set; } = string.Empty;
    public int CharLength { get; set; }
    public string SentAtUtc { get; set; } = string.Empty;
}

public static class PromptLastSentStore
{
    private static readonly ConcurrentDictionary<string, LastSentPromptSnapshot> Store = new(StringComparer.Ordinal);

    public static void Record(string sessionId, LastSentPromptSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || snapshot == null) return;
        Store[sessionId.Trim()] = snapshot;
    }

    public static LastSentPromptSnapshot? Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return Store.TryGetValue(sessionId.Trim(), out var s) ? s : null;
    }

    public static void ClearForTests() => Store.Clear();
}
