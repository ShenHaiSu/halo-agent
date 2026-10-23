using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// need21-B03: 会话图片暂存（原嵌于 HostBridgeFacade.PendingSessionImages）。
/// 中立化至 Infrastructure，Bridge 与 Services 均可引用，断开 Services→Bridge 反向依赖。
/// startAgentTurn 落盘 Put → Orchestrator 轮首 Take 并入 user 消息 Images。
/// </summary>
public static class SessionImageStash
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        List<ChatImageRef>
    > _map = new(StringComparer.Ordinal);

    public static void Put(string sessionId, List<ChatImageRef> refs)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || refs == null || refs.Count == 0)
            return;
        _map[sessionId.Trim()] = refs;
    }

    public static List<ChatImageRef> Take(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _map.TryRemove(sessionId.Trim(), out var refs))
            return refs;
        return new List<ChatImageRef>();
    }

    public static void Clear(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            _map.TryRemove(sessionId.Trim(), out _);
    }
}
