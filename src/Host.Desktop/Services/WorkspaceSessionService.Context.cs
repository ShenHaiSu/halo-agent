using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed record CompactCommitResult(long TurnSeq, string? MetadataWarning);

public sealed partial class WorkspaceSessionService
{
    /// <summary>在同一事务中提交桩消息与 compact 轮，随后容错刷新元数据。</summary>
    public async Task<CompactCommitResult> AppendCompactTurnAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage stubMessage,
        TurnSummary stubTurn,
        CancellationToken ct = default
    )
    {
        if (stubMessage == null || string.IsNullOrWhiteSpace(stubMessage.Id))
            throw new ArgumentException("桩消息不能为空", nameof(stubMessage));
        if (!string.Equals(stubMessage.Kind, MessageKind.CompactStub, StringComparison.Ordinal))
            throw new ArgumentException("桩消息 kind 必须为 compact_stub", nameof(stubMessage));
        if (!string.Equals(stubMessage.Role, "user", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("桩消息 role 必须为 user", nameof(stubMessage));
        if (stubTurn == null || string.IsNullOrWhiteSpace(stubTurn.TurnId))
            throw new ArgumentException("桩轮不能为空", nameof(stubTurn));
        if (!string.Equals(stubTurn.Kind, TurnKind.Compact, StringComparison.Ordinal))
            throw new ArgumentException("桩轮 kind 必须为 compact", nameof(stubTurn));
        if (!string.Equals(stubTurn.TurnId, "cturn-" + stubMessage.Id, StringComparison.Ordinal))
            throw new ArgumentException("桩轮 id 必须与桩消息匹配", nameof(stubTurn));
        if (stubTurn.Blocks != 0 || stubTurn.FinalBlockId != null)
            throw new ArgumentException("compact 轮不能包含 block 或 finalBlockId", nameof(stubTurn));

        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        long turnSeq;
        try
        {
            turnSeq = await _sessionRepository
                .AppendCompactStubAsync(workspaceId, sessionId, stubMessage, stubTurn, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Error(
                    "[Compact] compact.stub.commit.failed ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " msg="
                        + stubMessage.Id
                        + " turn="
                        + stubTurn.TurnId
                        + " error="
                        + ex.GetType().Name,
                    ex
                );
            }
            catch { }
            throw;
        }

        if (meta == null)
        {
            const string warning = "COMPACT_METADATA_WARNING: SESSION_META_MISSING";
            try
            {
                HaloLogger.App.Warn(
                    "[Compact] compact.meta.refresh.failed ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " reason=SESSION_META_MISSING"
                );
            }
            catch { }
            return new CompactCommitResult(turnSeq, warning);
        }

        meta.MessageCount += 1;
        if (string.IsNullOrWhiteSpace(meta.CompactMode))
            meta.CompactMode = CompactMode.Stub;
        meta.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            return new CompactCommitResult(turnSeq, null);
        }
        catch (Exception ex)
        {
            string warning = "COMPACT_METADATA_WARNING: " + ex.GetType().Name;
            try
            {
                HaloLogger.App.Warn(
                    "[Compact] compact.meta.refresh.failed ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " error="
                        + ex.GetType().Name,
                    ex
                );
            }
            catch { }
            return new CompactCommitResult(turnSeq, warning);
        }
    }

    /// <summary>活跃切片（真正进模型上下文的那段）。无桩会话等价于全量。</summary>
    public async Task<List<UnifiedChatMessage>> GetActiveContextMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            var tail = await _sessionRepository
                .GetActiveContextMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            return Context.ActiveContext.Slice(tail);
        }
        catch
        {
            var full = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            return Context.ActiveContext.Slice(full);
        }
    }

    /// <summary>最后一个压缩桩（无桩返回 null）。DB 优先，旧会话回退扫全量。</summary>
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
                var message = full[i];
                if (message == null || !Context.ActiveContext.IsStub(message))
                    continue;
                int removed = 0;
                if (!string.IsNullOrWhiteSpace(message.MetaJson))
                {
                    try
                    {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<CompactStubMeta>(
                            message.MetaJson,
                            HaloJsonOptions.CompactCamelCase
                        );
                        if (meta != null)
                            removed = meta.RemovedCount;
                    }
                    catch { }
                }
                return new CompactStubInfo
                {
                    MessageId = message.Id,
                    TurnId = string.Empty,
                    HasMatchingTurn = false,
                    Seq = null,
                    Summary = message.Content ?? string.Empty,
                    SummaryTokens = (int)(message.OutputTokens ?? 0),
                    PromptTokens = (int)(message.InputTokens ?? 0),
                    CachedTokens = (int)(message.CachedTokens ?? 0),
                    RemovedCount = removed,
                    CreatedAtUtc = message.CreatedAtUtc,
                };
            }
        }
        catch { }
        return null;
    }

    internal async Task<(long? FromSeq, long? ToSeq)> GetMessageSequenceRangeAsync(
        string workspaceId,
        string sessionId,
        string fromMessageId,
        string toMessageId,
        CancellationToken ct = default
    )
    {
        if (
            _sessionRepository is not SessionRepository concrete
            || string.IsNullOrWhiteSpace(fromMessageId)
            || string.IsNullOrWhiteSpace(toMessageId)
        )
            return (null, null);
        var store = new SessionHistoryStore(new SessionHistoryDb(concrete.Storage));
        return await store
            .GetMessageSequenceRangeAsync(workspaceId, sessionId, fromMessageId, toMessageId, ct)
            .ConfigureAwait(false);
    }
}
