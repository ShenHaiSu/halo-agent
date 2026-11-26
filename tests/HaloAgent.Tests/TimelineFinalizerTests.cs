#nullable enable
using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class TimelineFinalizerTests
{
    [Fact]
    public void FinalizeUserTurn_CancelledCreatesZeroBlockSummary()
    {
        var ctx = CreateContext("user-first", "request-cancel");

        var (blocks, turn) = TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled);

        Assert.Empty(blocks);
        Assert.Equal("uturn-user-first", turn.TurnId);
        Assert.Equal(0, turn.TurnSeq);
        Assert.Equal(0, turn.IterEnd);
        Assert.Equal(ctx.UserMessage!.Content, turn.UserContent);
        Assert.Equal(
            TimelineBlockTracker.BuildPreview(ctx.UserMessage.Content!, 15),
            turn.UserPreview
        );
        Assert.Null(turn.FinalBlockId);
        Assert.Equal(0, turn.Blocks);
        Assert.Equal(0, turn.Tools);
        Assert.Equal(0, turn.Failed);
        Assert.Equal(0, turn.DurationMs);
        Assert.Equal(0, turn.Chars);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal("request-cancel", turn.LastRequestId);
        Assert.Equal("turn", turn.Kind);
        Assert.Equal(0, turn.RemovedMessages);
        Assert.Equal(turn.TurnId, turn.AnchorKey);
        Assert.Equal(turn.CreatedAtUtc, turn.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(turn.CreatedAtUtc).Offset);
    }

    [Fact]
    public void FinalizeUserTurn_ErrorCreatesZeroBlockSummary()
    {
        var ctx = CreateContext("user-error", "request-error");

        var (blocks, turn) = TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Error);

        Assert.Empty(blocks);
        Assert.Equal("uturn-user-error", turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Error, turn.Status);
        Assert.Equal("request-error", turn.LastRequestId);
    }

    [Fact]
    public void FinalizeUserTurn_WithoutUserIsRejected()
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "workspace-test",
            SessionId = "sess-cancel",
            UserText = "orphan text",
            RequestId = "gateway-request",
            TurnRequestId = "request-cancel",
        };

        Assert.Throws<InvalidOperationException>(
            () => TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled)
        );
    }

    [Fact]
    public void FinalizeUserTurn_WithoutUserIdIsRejected()
    {
        var ctx = CreateContext(string.Empty, "request-cancel");

        Assert.Throws<InvalidOperationException>(
            () => TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled)
        );
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("truncated")]
    [InlineData("running")]
    [InlineData("")]
    public void FinalizeUserTurn_RejectsNonTerminalFallbackStatus(string status)
    {
        var ctx = CreateContext("user-first", "request-cancel");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimelineFinalizer.FinalizeUserTurn(ctx, status)
        );
    }

    [Fact]
    public void FinalizeUserTurn_IgnoresUncommittedDrafts()
    {
        var ctx = CreateContext("user-first", "request-cancel");
        var textDraft = new TimelineBlockDraft
        {
            BlockId = "draft-text",
            Kind = TimelineBlockKind.Text,
            Iteration = 1,
            Sequence = 1,
        };
        textDraft.Text.Append("partial answer");
        var toolDraft = new TimelineBlockDraft
        {
            BlockId = "draft-tool",
            Kind = TimelineBlockKind.ToolCall,
            CallId = "call-partial",
            FunctionName = "read_file",
            Iteration = 1,
            Sequence = 2,
        };
        toolDraft.Args.Append("{\"path\":");
        ctx.TimelineLog.Add(textDraft);
        ctx.TimelineLog.Add(toolDraft);

        var (blocks, turn) = TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled);

        Assert.Empty(blocks);
        Assert.Equal(0, turn.Blocks);
        Assert.Equal(0, turn.Tools);
        Assert.Equal(0, turn.Chars);
    }

    [Fact]
    public void FinalizeUserTurn_PreservesImagePlaceholderAndReferenceSemantics()
    {
        var ctx = CreateContext("user-image", "request-image");
        const string content = "请查看 [[image:img-first]]";
        ctx.UserMessage!.Content = content;
        ctx.UserMessage.Images = new List<ChatImageRef>
        {
            new()
            {
                ImageId = "img-first",
                FileName = "first.png",
                Mime = "image/png",
                RelPath = "attachments/first.png",
            },
        };

        var (_, turn) = TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled);

        Assert.Equal(content, turn.UserContent);
        Assert.Equal(TimelineBlockTracker.BuildPreview(content, 15), turn.UserPreview);
    }

    private static TurnContext CreateContext(string userId, string requestId) =>
        new()
        {
            WorkspaceId = "workspace-test",
            SessionId = "sess-cancel",
            UserText = "fallback text must not win",
            UserMessage = new UnifiedChatMessage
            {
                Id = userId,
                Role = "user",
                Content = "12345678901234567890",
            },
            RequestId = "gateway-request",
            TurnRequestId = requestId,
        };
}
