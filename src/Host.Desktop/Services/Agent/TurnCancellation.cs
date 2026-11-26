using System.Threading;

namespace Host.Desktop.Services;

/// <summary>
/// 单轮取消源载体：把「用户取消」与「本轮超时」两个语义分开。
/// User 只随用户停止而取消，级联所有子；Turn 只随本轮超时而取消，不级联到子；
/// Linked 是两者合并的令牌，全轮复用同一个预创建 CTS，由 Dispose 确定性释放。
/// </summary>
public sealed class TurnCancellation : IDisposable
{
    // 仅用于泄漏回归断言；编译期可见性由已有 InternalsVisibleTo 提供
    internal static int LiveLinkedSources;

    private readonly CancellationTokenSource _linked;
    private int _disposed;

    public CancellationToken User { get; }
    public CancellationToken Turn { get; }

    // 同一个 CTS，不重复创建
    public CancellationToken Linked => _linked.Token;

    public TurnCancellation(CancellationToken user, CancellationToken turn)
    {
        User = user;
        Turn = turn;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(user, turn);
        Interlocked.Increment(ref LiveLinkedSources);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _linked.Dispose();
        Interlocked.Decrement(ref LiveLinkedSources);
    }
}
