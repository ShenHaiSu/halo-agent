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

    /// <summary>P007：活跃切片（真正进模型上下文的那段）。无桩会话等价于全量。
    /// v2 单语句快路径（含前导 system）；失败/无库回退全量读 + 内存切片。</summary>
    public async Task<List<UnifiedChatMessage>> GetActiveContextMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        // P007：单语句快路径已含切片所需全部行；再经 ActiveContext.Slice 归一化（幂等）。
        try
        {
            var tail = await _sessionRepository
                .GetActiveContextMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            return Context.ActiveContext.Slice(tail);
        }
        catch { }
        var full = await _sessionRepository
            .GetMessagesAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        return Context.ActiveContext.Slice(full);
    }

    /// <summary>P007：最后一个压缩桩（无桩返回 null）。DB 优先，v1 纯 jsonl 回退扫全量。</summary>
    public async Task<CompactStubInfo?> GetLastCompactStubAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            if (_sessionRepository is SessionRepository concrete)
            {
                string dbPath = concrete.Storage.GetWorkspaceDbPath(workspaceId);
                if (File.Exists(dbPath))
                {
                    var store = new SessionHistoryStore(new SessionHistoryDb(concrete.Storage));
                    var info = await store
                        .GetLastCompactStubAsync(workspaceId, sessionId, ct)
                        .ConfigureAwait(false);
                    if (info != null)
                        return info;
                }
            }
        }
        catch { }
        try
        {
            var full = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            for (int i = full.Count - 1; i >= 0; i--)
            {
                var m = full[i];
                if (m == null || !Context.ActiveContext.IsStub(m))
                    continue;
                int removed = 0;
                if (!string.IsNullOrWhiteSpace(m.MetaJson))
                {
                    try
                    {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<CompactStubMeta>(
                            m.MetaJson,
                            HaloJsonOptions.CompactCamelCase
                        );
                        if (meta != null)
                            removed = meta.RemovedCount;
                    }
                    catch { }
                }
                return new CompactStubInfo
                {
                    MessageId = m.Id,
                    TurnId = "cturn-" + m.Id,
                    Seq = null,
                    Summary = m.Content ?? string.Empty,
                    SummaryTokens = (int)(m.OutputTokens ?? 0),
                    PromptTokens = (int)(m.InputTokens ?? 0),
                    CachedTokens = (int)(m.CachedTokens ?? 0),
                    RemovedCount = removed,
                    CreatedAtUtc = m.CreatedAtUtc,
                };
            }
        }
        catch { }
        return null;
    }
}
