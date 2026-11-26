using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Host.Desktop.Services;

/// <summary>
/// 工作区生命周期读写门：创建、子会话创建、消息追加与 Agent 启动持共享租约，
/// 会话树删除持独占租约，避免删除期间新建同树节点或追加消息。
/// </summary>
public sealed class WorkspaceLifecycleGate
{
    public static WorkspaceLifecycleGate Shared { get; } = new();

    private readonly ConcurrentDictionary<string, GateState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public IDisposable? TryAcquireShared(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("Workspace ID cannot be empty.", nameof(workspaceId));

        var state = _states.GetOrAdd(workspaceId.Trim(), _ => new GateState());
        lock (state.Sync)
        {
            if (state.ExclusiveHeld || state.WaitingExclusive > 0)
                return null;
            state.SharedHolders++;
            return new WorkspaceLifecycleLease(state, exclusive: false);
        }
    }

    public async Task<IDisposable?> TryAcquireExclusiveAsync(
        string workspaceId,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("Workspace ID cannot be empty.", nameof(workspaceId));
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var state = _states.GetOrAdd(workspaceId.Trim(), _ => new GateState());
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                TaskCompletionSource<bool>? waiter = null;
                lock (state.Sync)
                {
                    if (!state.ExclusiveHeld && state.SharedHolders == 0)
                    {
                        state.ExclusiveHeld = true;
                        return new WorkspaceLifecycleLease(state, exclusive: true);
                    }

                    state.WaitingExclusive++;
                    waiter = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    state.ExclusiveWaiters.AddLast(waiter);
                }

                try
                {
                    await waiter.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch
                {
                    lock (state.Sync)
                    {
                        if (!waiter.Task.IsCompleted)
                        {
                            state.WaitingExclusive--;
                            state.ExclusiveWaiters.Remove(waiter);
                        }
                        else
                        {
                            // 已获授予但等待方被取消时，把独占权转交下一位等待者。
                            PromoteWaiter(state);
                        }
                    }
                    throw;
                }
                return new WorkspaceLifecycleLease(state, exclusive: true);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal int WaitingExclusiveCount(string workspaceId)
    {
        if (
            string.IsNullOrWhiteSpace(workspaceId)
            || !_states.TryGetValue(workspaceId.Trim(), out var state)
        )
            return 0;
        lock (state.Sync)
            return state.WaitingExclusive;
    }

    public bool IsExclusiveHeld(string workspaceId) =>
        !string.IsNullOrWhiteSpace(workspaceId)
        && _states.TryGetValue(workspaceId.Trim(), out var state)
        && state.ExclusiveHeld;

    private static void PromoteWaiter(GateState state)
    {
        while (state.ExclusiveWaiters.Count > 0)
        {
            var next = state.ExclusiveWaiters.First!.Value;
            state.ExclusiveWaiters.RemoveFirst();
            state.WaitingExclusive--;
            if (next.TrySetResult(true))
            {
                state.ExclusiveHeld = true;
                return;
            }
        }

        state.ExclusiveHeld = false;
    }

    private sealed class GateState
    {
        public readonly object Sync = new();
        public readonly LinkedList<TaskCompletionSource<bool>> ExclusiveWaiters = new();
        public int SharedHolders;
        public int WaitingExclusive;
        public bool ExclusiveHeld;
    }

    private sealed class WorkspaceLifecycleLease : IDisposable
    {
        private readonly GateState _state;
        private readonly bool _exclusive;
        private int _released;

        public WorkspaceLifecycleLease(GateState state, bool exclusive)
        {
            _state = state;
            _exclusive = exclusive;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_state.Sync)
            {
                if (_exclusive)
                    PromoteWaiter(_state);
                else
                {
                    _state.SharedHolders--;
                    if (_state.SharedHolders == 0 && _state.WaitingExclusive > 0)
                        PromoteWaiter(_state);
                }
            }
        }
    }
}
