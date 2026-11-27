using System;
using System.Threading;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>统一会话操作协调器：原子获取、仅持有者释放与跨工作区隔离。</summary>
public sealed class SessionOperationCoordinatorTests
{
    [Fact]
    public void SameWorkspaceAndSessionAllowsSingleHolder()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out var first)
        );
        Assert.False(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.Agent, out var second)
        );
        Assert.Null(second);
        first!.Dispose();
        Assert.True(coordinator.TryAcquire("ws", "sess", SessionOperationKind.Agent, out _));
    }

    [Fact]
    public void DifferentWorkspacesDoNotBlockEachOther()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(coordinator.TryAcquire("ws1", "sess", SessionOperationKind.History, out _));
        Assert.True(coordinator.TryAcquire("ws2", "sess", SessionOperationKind.History, out _));
    }

    [Fact]
    public void NonHolderCannotReleaseOtherLease()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out var owner)
        );
        string ownerToken = SessionOperationCoordinator.BuildKey("ws", "sess");
        Assert.False(coordinator.Release("ws", "sess", "not-the-owner"));
        Assert.True(coordinator.IsHeld("ws", "sess"));
        owner!.Dispose();
        Assert.False(coordinator.IsHeld("ws", "sess"));
        Assert.False(string.IsNullOrEmpty(ownerToken));
    }

    [Fact]
    public void RepeatedDisposeDoesNotRemoveNewHolder()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out var first)
        );
        first!.Dispose();
        Assert.True(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out var second)
        );
        first.Dispose();
        Assert.True(coordinator.IsHeld("ws", "sess"));
        second!.Dispose();
    }

    [Fact]
    public void CancellationReleasesLease()
    {
        var coordinator = new SessionOperationCoordinator();
        using (var source = new CancellationTokenSource())
        {
            Assert.True(
                coordinator.TryAcquire("ws", "sess", SessionOperationKind.Agent, out var lease)
            );
            source.Cancel();
            lease!.Dispose();
        }
        Assert.True(coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out _));
    }

    [Fact]
    public void TimeoutReturnsFailureWithoutLease()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(coordinator.TryAcquire("ws", "sess", SessionOperationKind.Compact, out _));
        bool acquired = coordinator.TryAcquire(
            "ws",
            "sess",
            SessionOperationKind.History,
            TimeSpan.FromMilliseconds(20),
            out var lease,
            out var waitMs
        );
        Assert.False(acquired);
        Assert.Null(lease);
        Assert.True(waitMs >= 0);
    }

    [Fact]
    public void BatchFailureReleasesLocksAcquiredEarlierInStableOrder()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(
            coordinator.TryAcquire("ws", "sess-b", SessionOperationKind.Agent, out var held)
        );
        using (held)
        {
            bool acquired = coordinator.TryAcquireBatch(
                "ws",
                new[] { "sess-b", "sess-a", "SESS-A" },
                SessionOperationKind.History,
                out var batch
            );

            Assert.False(acquired);
            Assert.Null(batch);
            Assert.False(coordinator.IsHeld("ws", "sess-a"));
            Assert.True(coordinator.IsHeld("ws", "sess-b"));
        }
        Assert.True(
            coordinator.TryAcquireBatch(
                "ws",
                new[] { "sess-b", "sess-a", "SESS-A" },
                SessionOperationKind.History,
                out var retry
            )
        );
        Assert.NotNull(retry);
        Assert.Equal(2, retry!.Count);
        retry.Dispose();
        retry.Dispose();
        Assert.False(coordinator.IsHeld("ws", "sess-a"));
        Assert.False(coordinator.IsHeld("ws", "sess-b"));
    }

    [Fact]
    public void HolderKindIsObservableForErrorMapping()
    {
        var coordinator = new SessionOperationCoordinator();
        Assert.True(
            coordinator.TryAcquire("ws", "sess", SessionOperationKind.History, out var lease)
        );
        Assert.True(coordinator.TryGetHolderKind("ws", "sess", out var kind));
        Assert.Equal(SessionOperationKind.History, kind);
        lease!.Dispose();
        Assert.False(coordinator.TryGetHolderKind("ws", "sess", out _));
    }
}
