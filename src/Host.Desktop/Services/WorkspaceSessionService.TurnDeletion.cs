using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed partial class WorkspaceSessionService
{
    /// <summary>
    /// 删除锚点所属整轮：先校验与备份，再用单事务提交 messages/turns/blocks；
    /// 提交后的元数据与孤儿文件清理失败只告警，绝不把已提交误报为未提交。
    /// </summary>
    public async Task<HistoryTurnDeleteResult> DeleteSessionTurnAsync(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        CancellationToken ct = default,
        SessionOperationLease? lease = null
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryTurnDeleteResult.Fail("SESSION_NOT_FOUND");
        if (string.IsNullOrWhiteSpace(anchorMessageId))
            return HistoryTurnDeleteResult.Fail("ANCHOR_NOT_FOUND");

        using var operationLease = lease ?? AcquireHistoryLease(workspaceId, sessionId);
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryTurnDeleteResult.Fail("SESSION_NOT_FOUND");

            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            int anchorIndex = messages.FindIndex(m =>
                string.Equals(m.Id, anchorMessageId, StringComparison.OrdinalIgnoreCase)
            );
            if (anchorIndex < 0)
                return HistoryTurnDeleteResult.Fail("ANCHOR_NOT_FOUND");
            string anchorRole = (messages[anchorIndex].Role ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            if (anchorRole != "user" && anchorRole != "assistant")
                return HistoryTurnDeleteResult.Fail("INVALID_ANCHOR_ROLE");
            if (!HistoryTurnSlice.TryResolve(messages, anchorMessageId, out var slice))
                return HistoryTurnDeleteResult.Fail("ANCHOR_NOT_FOUND");

            string turnId = "uturn-" + messages[slice.UserIndex].Id;
            var keptRaw = messages
                .Take(slice.UserIndex)
                .Concat(messages.Skip(slice.EndExclusive))
                .ToList();
            var (fixed_, synthetic, removedOrphans) = RebuildHealedHistory(keptRaw);
            int deletedMessages = slice.EndExclusive - slice.UserIndex;

            var store = new SessionHistoryStore(new SessionHistoryDb(_sessionRepository.Storage));
            bool projectionDeleted = false;
            try
            {
                var summaries = await store
                    .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
                projectionDeleted = summaries.Any(s =>
                    string.Equals(s.TurnId, turnId, StringComparison.Ordinal)
                );
            }
            catch
            {
                // 孤立轮没有摘要也要能删；投影缺失只体现在结果里，不阻断删除。
                projectionDeleted = false;
            }

            string backupPath;
            try
            {
                var backup = await new WorkspaceSessionStore(_sessionRepository.Storage)
                    .BackupWorkspaceAsync(workspaceId, ct)
                    .ConfigureAwait(false);
                backupPath = backup.BackupPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(backupPath))
                    return HistoryTurnDeleteResult.Fail("BACKUP_FAILED");
            }
            catch (Exception ex)
            {
                HaloLogger.App.Error(
                    $"[History] turn delete backup failed ws={workspaceId} sess={sessionId} turn={turnId}",
                    ex
                );
                return HistoryTurnDeleteResult.Fail("BACKUP_FAILED");
            }

            try
            {
                await store
                    .DeleteTurnAndRewriteMessagesAsync(workspaceId, sessionId, turnId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Error(
                    $"[History] turn delete commit failed ws={workspaceId} sess={sessionId} turn={turnId}",
                    ex
                );
                return HistoryTurnDeleteResult.Fail("DELETE_COMMIT_FAILED");
            }

            var warnings = new List<string>();
            try
            {
                meta.MessageCount = fixed_.Count;
                var lastSnap = fixed_.LastOrDefault(m =>
                    string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && m.InputTokens.HasValue
                    && m.InputTokens.Value > 0
                );
                meta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
                meta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
                meta.UpdatedAtUtc = DateTime.UtcNow;
                await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add("METADATA_REFRESH_FAILED");
                HaloLogger.App.Warn(
                    $"[History] turn delete metadata refresh failed ws={workspaceId} sess={sessionId} turn={turnId}: "
                        + ex.GetType().Name
                );
            }
            try
            {
                await store
                    .CollectOrphanToolOutputsAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add("ORPHAN_CLEANUP_FAILED");
                HaloLogger.App.Warn(
                    $"[History] turn delete orphan cleanup failed ws={workspaceId} sess={sessionId} turn={turnId}: "
                        + ex.GetType().Name
                );
            }

            string? metadataWarning = warnings.Count > 0 ? string.Join(";", warnings) : null;
            HaloLogger.App.Info(
                $"[History] turn delete ws={workspaceId} sess={sessionId} turn={turnId}"
                    + $" deleted={deletedMessages} kept={fixed_.Count} synthetic={synthetic}"
                    + $" orphans={removedOrphans} bak={Path.GetFileName(backupPath)}"
            );
            return HistoryTurnDeleteResult.Ok(
                turnId,
                deletedMessages,
                fixed_.Count,
                synthetic,
                removedOrphans,
                Path.GetFileName(backupPath),
                metadataWarning,
                projectionDeleted
            );
        }
        finally
        {
            appendLock.Release();
        }
    }
}
