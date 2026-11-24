using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Host.Desktop.Services;

/// <summary>会话操作类型：同一会话同一时刻只允许一种操作持有领域锁。</summary>
public enum SessionOperationKind
{
    Agent,
    Compact,
    History
}

/// <summary>领域锁租约：只能由持有者释放一次，Dispose 幂等。</summary>
public sealed class SessionOperationLease : IDisposable
{
    private readonly SessionOperationCoordinator _coordinator;
    private readonly string _key;
    private int _released;

    internal SessionOperationLease(
        SessionOperationCoordinator coordinator,
        string key,
        string owner,
        string workspaceId,
        string sessionId,
        SessionOperationKind kind
    )
    {
        _coordinator = coordinator;
        _key = key;
        Owner = owner;
        WorkspaceId = workspaceId;
        SessionId = sessionId;
        Kind = kind;
    }

    internal string Owner { get; }

    public string WorkspaceId { get; }

    public string SessionId { get; }

    public SessionOperationKind Kind { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _coordinator.ReleaseByOwner(_key, Owner);
    }
}

/// <summary>
/// 统一会话操作协调器：发送、压缩与全部历史写入口共用原子锁。
/// 锁键由工作区与会话组成，使用序数比较，避免不同工作区下同名会话相互阻塞。
/// </summary>
public sealed class SessionOperationCoordinator
{
    /// <summary>宿主与领域服务共享的进程内协调器；DI 也可注入独立实例用于隔离测试。</summary>
    public static SessionOperationCoordinator Shared { get; } = new();

    private sealed record Holder(string Owner, SessionOperationKind Kind);

    private readonly ConcurrentDictionary<string, Holder> _holders = new(StringComparer.Ordinal);

    public static string BuildKey(string? workspaceId, string? sessionId) =>
        (workspaceId ?? string.Empty).Trim() + "\n" + (sessionId ?? string.Empty).Trim();

    public bool TryAcquire(
        string workspaceId,
        string sessionId,
        SessionOperationKind kind,
        out SessionOperationLease? lease
    ) => TryAcquire(workspaceId, sessionId, kind, TimeSpan.Zero, out lease, out _);

    public bool TryAcquire(
        string workspaceId,
        string sessionId,
        SessionOperationKind kind,
        TimeSpan timeout,
        out SessionOperationLease? lease,
        out long lockWaitMs
    )
    {
        string key = BuildKey(workspaceId, sessionId);
        string owner = Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_holders.TryAdd(key, new Holder(owner, kind)))
            {
                lockWaitMs = watch.ElapsedMilliseconds;
                lease = new SessionOperationLease(
                    this,
                    key,
                    owner,
                    workspaceId ?? string.Empty,
                    sessionId ?? string.Empty,
                    kind
                );
                return true;
            }
            if (timeout <= TimeSpan.Zero || watch.Elapsed >= timeout)
            {
                lockWaitMs = watch.ElapsedMilliseconds;
                lease = null;
                return false;
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>仅持有者可释放；重复释放或非持有者释放均不影响当前持有者。</summary>
    public bool Release(string workspaceId, string sessionId, string owner) =>
        ReleaseByOwner(BuildKey(workspaceId, sessionId), owner);

    internal bool ReleaseByOwner(string key, string owner) =>
        _holders.TryGetValue(key, out var holder)
        && string.Equals(holder.Owner, owner, StringComparison.Ordinal)
        && _holders.TryRemove(new KeyValuePair<string, Holder>(key, holder));

    public bool IsHeld(string workspaceId, string sessionId) =>
        _holders.ContainsKey(BuildKey(workspaceId, sessionId));

    public bool TryGetHolderKind(
        string workspaceId,
        string sessionId,
        out SessionOperationKind kind
    )
    {
        if (_holders.TryGetValue(BuildKey(workspaceId, sessionId), out var holder))
        {
            kind = holder.Kind;
            return true;
        }
        kind = SessionOperationKind.Agent;
        return false;
    }
}
