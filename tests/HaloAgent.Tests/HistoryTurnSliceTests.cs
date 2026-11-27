using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>统一轮次切面：重放与分支共享的整轮边界计算。</summary>
public sealed class HistoryTurnSliceTests
{
    private static List<HistoryMessageRef> Messages() =>
        new()
        {
            new HistoryMessageRef("u1", "user", null),
            new HistoryMessageRef("a1", "assistant", null),
            new HistoryMessageRef("tool-a", "tool", null),
            new HistoryMessageRef("a2", "assistant", null),
            new HistoryMessageRef("u2", "user", null),
            new HistoryMessageRef("a3", "assistant", null),
        };

    [Fact]
    public void UserAnchorCoversWholeTurnUntilNextUser()
    {
        Assert.True(HistoryTurnSlice.TryResolve(Messages(), "u1", out var slice));
        Assert.Equal(0, slice.UserIndex);
        Assert.Equal(4, slice.EndExclusive);
    }

    [Fact]
    public void MiddleAssistantAnchorCoversOwningTurn()
    {
        Assert.True(HistoryTurnSlice.TryResolve(Messages(), "a1", out var slice));
        Assert.Equal(0, slice.UserIndex);
        Assert.Equal(4, slice.EndExclusive);
    }

    [Fact]
    public void LastAssistantAnchorEndsAtMessageCount()
    {
        Assert.True(HistoryTurnSlice.TryResolve(Messages(), "a3", out var slice));
        Assert.Equal(4, slice.UserIndex);
        Assert.Equal(6, slice.EndExclusive);
    }

    [Fact]
    public void ToolAnchorLocatesOwningUser()
    {
        Assert.True(HistoryTurnSlice.TryResolve(Messages(), "tool-a", out var slice));
        Assert.Equal(0, slice.UserIndex);
        Assert.Equal(4, slice.EndExclusive);
    }

    [Fact]
    public void MissingPriorUserFails()
    {
        var refs = new List<HistoryMessageRef> { new("a1", "assistant", null) };
        Assert.False(HistoryTurnSlice.TryResolve(refs, "a1", out _));
    }

    [Fact]
    public void EmptyAnchorFails()
    {
        Assert.False(HistoryTurnSlice.TryResolve(Messages(), "", out _));
    }

    [Fact]
    public void ConsecutiveUsersKeepIndependentBoundaries()
    {
        var refs = new List<HistoryMessageRef>
        {
            new("u1", "user", null),
            new("u2", "user", null),
            new("a2", "assistant", null),
        };
        Assert.True(HistoryTurnSlice.TryResolve(refs, "u1", out var first));
        Assert.Equal(0, first.UserIndex);
        Assert.Equal(1, first.EndExclusive);
        Assert.True(HistoryTurnSlice.TryResolve(refs, "u2", out var second));
        Assert.Equal(1, second.UserIndex);
        Assert.Equal(3, second.EndExclusive);
    }

    [Fact]
    public void UnifiedChatMessagesUseSameBoundaryRules()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("first"),
            UnifiedChatMessage.Assistant("reply"),
            UnifiedChatMessage.User("second"),
        };
        Assert.True(HistoryTurnSlice.TryResolve(messages, messages[1].Id, out var slice));
        Assert.Equal(0, slice.UserIndex);
        Assert.Equal(2, slice.EndExclusive);
    }
}
