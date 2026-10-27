namespace HaloAgent.Desktop.Shell;

public sealed record RecentSession(string Id, string Title, DateTime ModifiedAt);

/// <summary>
/// 最近会话提供者。托盘菜单在引擎未就绪时也要能显示，所以读宿主侧缓存
/// （每次收到 session.list 后 Update），而非每次请求引擎。
/// </summary>
public interface IRecentSessions
{
    IReadOnlyList<RecentSession> List();
    void Update(IReadOnlyList<RecentSession> sessions);
}

public sealed class InMemoryRecentSessions : IRecentSessions
{
    private readonly object _lock = new();
    private IReadOnlyList<RecentSession> _items = Array.Empty<RecentSession>();

    public IReadOnlyList<RecentSession> List()
    {
        lock (_lock) return _items;
    }

    public void Update(IReadOnlyList<RecentSession> sessions)
    {
        lock (_lock) _items = sessions.OrderByDescending(s => s.ModifiedAt).ToArray();
    }
}

public sealed class EmptyRecentSessions : IRecentSessions
{
    public IReadOnlyList<RecentSession> List() => Array.Empty<RecentSession>();
    public void Update(IReadOnlyList<RecentSession> sessions) { /* 空实现：托盘显示“暂无” */ }
}
