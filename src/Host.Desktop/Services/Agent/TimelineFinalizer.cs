using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// Turn finalization: pending drafts are resolved to process or final in a single transaction.
/// The last non-empty text becomes final on success; other endings produce process only.
/// </summary>
public static class TimelineFinalizer
{
    public static (List<TimelineBlock> Blocks, TurnSummary Turn) Finalize(
        TurnContext ctx,
        int iteration,
        bool truncated
    ) =>
        Finalize(
            ctx,
            iteration,
            truncated ? TimelineTurnStatus.Truncated : TimelineTurnStatus.Completed
        );

    public static (List<TimelineBlock> Blocks, TurnSummary Turn) Finalize(
        TurnContext ctx,
        int iteration,
        string status
    )
    {
        string userId = ctx.UserMessage?.Id ?? ctx.RequestId ?? string.Empty;
        string turnId = "uturn-" + userId;
        var drafts = ctx.TimelineLog ?? new List<TimelineBlockDraft>();

        var blocks = new List<TimelineBlock>(drafts.Count);
        TimelineBlock? finalCandidate = null;

        foreach (var draft in drafts)
        {
            string raw =
                draft.Kind == TimelineBlockKind.ToolCall
                    ? draft.Args.ToString()
                    : draft.Text.ToString();
            if (draft.Kind == TimelineBlockKind.Text && string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            if (draft.Kind == TimelineBlockKind.Reasoning && string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string content = raw;
            string preview = TimelineBlockTracker.BuildPreview(content, 200);
            var block = new TimelineBlock
            {
                BlockId = draft.BlockId,
                TurnId = turnId,
                Iteration = draft.Iteration <= 0 ? iteration : draft.Iteration,
                Sequence = draft.Sequence,
                Kind = draft.Kind,
                Tag = TimelineBlockTag.Process,
                CallId = draft.CallId,
                CharCount = content.Length,
                Preview = preview,
                Content = content,
                ContentRef = null,
                Truncated = false,
            };
            blocks.Add(block);
        }

        blocks.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

        string endedStatus = NormalizeEndedStatus(status);
        string? finalBlockId = null;

        if (string.Equals(endedStatus, TimelineTurnStatus.Completed, StringComparison.Ordinal))
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var b = blocks[i];
                if (
                    string.Equals(b.Kind, TimelineBlockKind.Text, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(b.Content)
                )
                {
                    b.Tag = TimelineBlockTag.Final;
                    finalCandidate = b;
                    finalBlockId = b.BlockId;
                    break;
                }
            }
        }

        int tools = 0;
        int chars = 0;
        foreach (var b in blocks)
        {
            if (string.Equals(b.Kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal))
            {
                tools++;
            }
            chars += b.CharCount;
        }

        string userPreview = TimelineBlockTracker.BuildPreview(ctx.UserText ?? string.Empty, 15);
        string now = DateTime.UtcNow.ToString("o");
        var turn = new TurnSummary
        {
            TurnId = turnId,
            TurnSeq = 0,
            IterEnd = iteration,
            UserPreview = userPreview,
            FinalBlockId = finalBlockId,
            FinalPreview =
                finalCandidate == null
                    ? string.Empty
                    : TimelineBlockTracker.BuildPreview(finalCandidate.Content, 60),
            Blocks = blocks.Count,
            Tools = tools,
            Failed = 0,
            DurationMs = 0,
            Chars = chars,
            Status = endedStatus,
            LastRequestId = ctx.RequestId ?? string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AnchorKey = turnId,
        };

        return (blocks, turn);
    }

    private static string NormalizeEndedStatus(string? status)
    {
        if (string.Equals(status, TimelineTurnStatus.Truncated, StringComparison.Ordinal))
            return TimelineTurnStatus.Truncated;
        if (string.Equals(status, TimelineTurnStatus.Cancelled, StringComparison.Ordinal))
            return TimelineTurnStatus.Cancelled;
        if (string.Equals(status, TimelineTurnStatus.Error, StringComparison.Ordinal))
            return TimelineTurnStatus.Error;
        return TimelineTurnStatus.Completed;
    }
}
