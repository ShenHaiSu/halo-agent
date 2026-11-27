using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 轮次终态统一落盘：缺失展示轮时只使用真实用户消息补建，已有轮只更新终态。
/// 清理不受正在取消的令牌影响，失败通过返回值显式告知事件发布方。
/// </summary>
public sealed partial class AgentOrchestrator
{
    private static string TimelineTurnId(TurnContext ctx) =>
        ctx.UserMessage != null && !string.IsNullOrWhiteSpace(ctx.UserMessage.Id)
            ? "uturn-" + ctx.UserMessage.Id
            : string.Empty;

    private async Task<bool> MarkTimelineTurnEndedAsync(TurnContext ctx, string status)
    {
        if (
            _storageManager == null
            || ctx == null
            || ctx.UserMessage == null
            || string.IsNullOrWhiteSpace(ctx.UserMessage.Id)
        )
        {
            return false;
        }
        string turnId = TimelineTurnId(ctx);
        try
        {
            TurnSummary? fallback =
                string.Equals(status, TimelineTurnStatus.Cancelled, StringComparison.Ordinal)
                || string.Equals(status, TimelineTurnStatus.Error, StringComparison.Ordinal)
                    ? TimelineFinalizer.FinalizeUserTurn(ctx, status).Turn
                    : null;
            var store = new Infrastructure.Storage.SessionHistoryStore(_storageManager);
            return await store
                .EnsureTurnEndedAsync(
                    ctx.WorkspaceId,
                    ctx.SessionId,
                    turnId,
                    status,
                    fallback,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Infrastructure.Logging.HaloLogger.App.Warn(
                "[Timeline] turn finalize failed sess="
                    + ctx.SessionId
                    + " turn="
                    + turnId
                    + " request="
                    + ctx.TurnRequestId
                    + " status="
                    + status,
                ex
            );
            return false;
        }
    }

    private async Task FinalizeCancelledTurnAsync(
        TurnContext ctx,
        string sessionId,
        int iteration,
        string? reason = null
    )
    {
        bool finalized = await MarkTimelineTurnEndedAsync(ctx, TimelineTurnStatus.Cancelled)
            .ConfigureAwait(false);
        PublishTurnEvent(
            sessionId,
            "TurnCancelled",
            new
            {
                iteration,
                reason,
                userMessagePersisted = ctx.UserMessage != null
                    && !string.IsNullOrWhiteSpace(ctx.UserMessage.Id),
                timelineFinalized = finalized,
            }
        );
    }
}
