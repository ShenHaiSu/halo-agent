using System;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>工作区生命周期门：共享租约、独占等待、超时与取消不泄漏。</summary>
public sealed class WorkspaceLifecycleGateTests
{
    [Fact]
    public async Task WaitingExclusiveBlocksNewSharedLeasesAndIsGrantedAfterReadersRelease()
    {
        var gate = new WorkspaceLifecycleGate();
        IDisposable firstReader = gate.TryAcquireShared("ws-lifecycle")!;
        Task<IDisposable?> exclusiveTask = gate.TryAcquireExclusiveAsync(
            "ws-lifecycle",
            TimeSpan.FromSeconds(2)
        );
        Assert.True(
            SpinWait.SpinUntil(
                () => gate.WaitingExclusiveCount("ws-lifecycle") == 1,
                TimeSpan.FromSeconds(2)
            )
        );

        Assert.Null(gate.TryAcquireShared("ws-lifecycle"));
        firstReader.Dispose();
        IDisposable? exclusive = await exclusiveTask;
        Assert.NotNull(exclusive);
        Assert.True(gate.IsExclusiveHeld("ws-lifecycle"));
        Assert.Null(gate.TryAcquireShared("ws-lifecycle"));
        exclusive!.Dispose();
        using IDisposable? nextReader = gate.TryAcquireShared("ws-lifecycle");
        Assert.NotNull(nextReader);
    }

    [Fact]
    public async Task ExclusiveTimeoutDoesNotDisturbExistingSharedLease()
    {
        var gate = new WorkspaceLifecycleGate();
        using IDisposable reader = gate.TryAcquireShared("ws-lifecycle")!;

        IDisposable? exclusive = await gate.TryAcquireExclusiveAsync(
            "ws-lifecycle",
            TimeSpan.FromMilliseconds(30)
        );

        Assert.Null(exclusive);
        Assert.False(gate.IsExclusiveHeld("ws-lifecycle"));
        using IDisposable? secondReader = gate.TryAcquireShared("ws-lifecycle");
        Assert.NotNull(secondReader);
    }

    [Fact]
    public async Task CancelledExclusiveWaiterDoesNotCorruptGate()
    {
        var gate = new WorkspaceLifecycleGate();
        using IDisposable reader = gate.TryAcquireShared("ws-lifecycle")!;
        using var source = new CancellationTokenSource();
        Task<IDisposable?> waiting = gate.TryAcquireExclusiveAsync(
            "ws-lifecycle",
            TimeSpan.FromSeconds(5),
            source.Token
        );
        Assert.True(
            SpinWait.SpinUntil(
                () => gate.WaitingExclusiveCount("ws-lifecycle") == 1,
                TimeSpan.FromSeconds(2)
            )
        );
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        IDisposable? timedOut = await gate.TryAcquireExclusiveAsync(
            "ws-lifecycle",
            TimeSpan.FromMilliseconds(30)
        );
        Assert.Null(timedOut);
        reader.Dispose();
        using IDisposable exclusive = (
            await gate.TryAcquireExclusiveAsync("ws-lifecycle", TimeSpan.FromSeconds(1))
        )!;
        Assert.True(gate.IsExclusiveHeld("ws-lifecycle"));
    }
}
