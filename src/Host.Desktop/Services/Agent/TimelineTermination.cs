namespace Host.Desktop.Services;

/// <summary>
/// 发货加固：轮次以非完成态终结时，把终态徽章落盘并把残留 final 降回 process。
/// 仅 v2 会话（HistoryVersion == 2）生效；v1 无时间轴库，直接跳过。
/// 任何异常内部吞掉，永不影响轮次原有终结语义。
/// </summary>
public sealed partial class AgentOrchestrator
{
    private static string TimelineTurnId(TurnContext ctx) =>
        "uturn-" + (ctx.UserMessage?.Id ?? ctx.RequestId ?? string.Empty);

    private async Task MarkTimelineTurnEndedAsync(TurnContext ctx, string status)
    {
        try
        {
            if (_storageManager == null || ctx == null)
            {
                return;
            }
            if (ctx.Session != null && ctx.Session.HistoryVersion != 2)
            {
                return;
            }
            string turnId = TimelineTurnId(ctx);
            if (
                string.IsNullOrWhiteSpace(turnId)
                || string.Equals(turnId, "uturn-", StringComparison.Ordinal)
            )
            {
                return;
            }
            var store = new Infrastructure.Storage.SessionHistoryStore(_storageManager);
            await store
                .MarkTurnEndedAsync(
                    ctx.WorkspaceId,
                    ctx.SessionId,
                    turnId,
                    status,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch { }
    }
}
