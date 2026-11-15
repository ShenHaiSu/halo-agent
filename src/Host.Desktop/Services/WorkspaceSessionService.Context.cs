using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>P007：压缩桩服务门面（M1 写入侧；M2 读侧门面同文件扩展）。</summary>
public sealed partial class WorkspaceSessionService
{
    /// <summary>P007：桩 turn 落盘 + meta 冻结（只改 MessageCount/UpdatedAt/CompactMode；账单不动）。
    /// 调用方须已持有会话追加锁（与 AppendSessionMessageAsync 同一把锁）。</summary>
    public async Task<long> AppendCompactTurnAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage stubMessage,
        TurnSummary stubTurn,
        CancellationToken ct = default
    )
    {
        if (stubMessage == null || string.IsNullOrWhiteSpace(stubMessage.Id))
            throw new ArgumentException("桩消息不能为空", nameof(stubMessage));
        if (stubTurn == null || string.IsNullOrWhiteSpace(stubTurn.TurnId))
            throw new ArgumentException("桩轮不能为空", nameof(stubTurn));

        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);

        long turnSeq = 0;
        // v2 才有 turns/DB；v1 纯 jsonl 会话跳过 DB（jsonl 已由 AppendMessageAsync 落盘）。
        bool isV2 = meta != null && meta.HistoryVersion == 2;
        if (isV2)
        {
            try
            {
                var store = new SessionHistoryStore(
                    new SessionHistoryDb(_sessionRepository.Storage)
                );
                turnSeq = await store
                    .InsertCompactStubAsync(workspaceId, sessionId, stubMessage, stubTurn, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    Infrastructure.Logging.HaloLogger.App.Error(
                        $"[Compact] InsertCompactStub failed ws={workspaceId} sess={sessionId}: {ex.Message}",
                        ex
                    );
                }
                catch { }
                throw;
            }
        }

        if (meta != null)
        {
            // P007：append-only 只增不减；账单（Cumulative*/Total*）与标题一律不动。
            meta.MessageCount += 1;
            if (string.IsNullOrWhiteSpace(meta.CompactMode))
                meta.CompactMode = CompactMode.Stub;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
        }
        return turnSeq;
    }
}
