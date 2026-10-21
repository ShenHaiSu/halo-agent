using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

public sealed partial class WorkspaceSessionService : IWorkspaceSessionService
{
    public async Task<bool> AppendSessionMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    )
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var appendLock = _sessionAppendLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. 追加写至 messages.jsonl
            await _sessionRepository
                .AppendMessageAsync(workspaceId, sessionId, message, ct)
                .ConfigureAwait(false);

            // 2. 更新会话元数据
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
            {
                meta = new SessionMetadata
                {
                    Id = sessionId,
                    WorkspaceId = workspaceId,
                    SessionHash = HeaderVariableResolver.ComputeDeterministicHash16(
                        $"{workspaceId}:{sessionId}"
                    ),
                    CreatedAtUtc = DateTime.UtcNow
                };
            }

            meta.MessageCount++;
            // need04-05：IN/缓存快照语义（有精确值则覆盖），OUT 为增量可加
            if (message.InputTokens.HasValue && message.InputTokens.Value > 0)
                meta.TotalInputTokens = message.InputTokens.Value;
            else
                meta.TotalInputTokens += TokenBudgetService.EstimateMessageTokens(message);
            if (message.OutputTokens.HasValue)
                meta.TotalOutputTokens += message.OutputTokens.Value;
            if (message.CachedTokens.HasValue && message.CachedTokens.Value > 0)
                meta.TotalCachedTokens = message.CachedTokens.Value;
            // need05-01：Σ账单累计（每次 usage 到账累加；user/tool 估算分支不碰 Cumulative；compact 重写保留，clear 清零）
            if (message.InputTokens.HasValue && message.InputTokens.Value > 0)
                meta.CumulativePromptTokens += message.InputTokens.Value;
            if (message.OutputTokens.HasValue && message.OutputTokens.Value > 0)
                meta.CumulativeCompletionTokens += message.OutputTokens.Value;
            meta.UpdatedAtUtc = DateTime.UtcNow;

            // need03-03 T1：首条 user 且未冻结时取第一句话前 5 字（与前端 instantTitleFromUserText 同语义，surrogate 安全）
            // need13：子会话标题已锁定（sub: label），跳过自动精炼
            if (
                !string.Equals(meta.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !meta.TitleLocked
                && (
                    string.IsNullOrWhiteSpace(meta.Title)
                    || meta.Title == "新建会话"
                    || meta.Title == "初始会话"
                )
            )
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    var trimmed = message.Content.Trim();
                    var firstLine =
                        trimmed
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? trimmed;
                    var firstSentence =
                        firstLine
                            .Split(
                                new[] { '。', '！', '？', '!', '?', ';', '；' },
                                StringSplitOptions.RemoveEmptyEntries
                            )
                            .FirstOrDefault()
                            ?.Trim() ?? firstLine.Trim();
                    var compact = new string(
                        firstSentence.Where(c => !char.IsWhiteSpace(c)).ToArray()
                    );
                    var elements = System.Globalization.StringInfo.ParseCombiningCharacters(
                        compact
                    );
                    int take = Math.Min(5, elements.Length);
                    var candidate =
                        take <= 0
                            ? string.Empty
                            : compact.Substring(
                                elements[0],
                                (take == elements.Length ? compact.Length : elements[take])
                                    - elements[0]
                            );
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        meta.Title = candidate;
                    }
                }
            }

            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);

            return true;
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>
    /// need05-03：Append 后半段“存 meta+刷 index”抽取，供压缩重写复用（纯搬代码，不改语义）。
    /// 注意：调用方须已持有会话级互斥（Append 持 _sessionAppendLocks[sessionId]，压缩持同一把锁）。
    /// </summary>
    internal async Task RefreshMetaIndexAsync(
        string workspaceId,
        string sessionId,
        SessionMetadata meta,
        CancellationToken ct = default
    )
    {
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        // 同步更新 sessions-index.json
        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx] = meta;
        }
        else
        {
            sessions.Insert(0, meta);
        }
        await _sessionRepository
            .SaveSessionsIndexAsync(workspaceId, sessions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>need05-03：供压缩服务持有追加锁（与 AppendSessionMessageAsync 同一把锁，天然互斥）。</summary>
    internal SemaphoreSlim GetSessionAppendLock(string sessionId) =>
        _sessionAppendLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public async Task<bool> UpdateSessionTitleAsync(
        string workspaceId,
        string sessionId,
        string newTitle,
        CancellationToken ct = default
    )
    {
        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
            return false;
        meta.Title = string.IsNullOrWhiteSpace(newTitle) ? "未命名会话" : newTitle.Trim();
        // need03-03：任何显式改名（含前端 LLM 精炼回写）即永久冻结，此后 Append 永不覆盖
        meta.TitleLocked = true;
        meta.UpdatedAtUtc = DateTime.UtcNow;
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx].Title = meta.Title;
            sessions[idx].TitleLocked = true;
            sessions[idx].UpdatedAtUtc = meta.UpdatedAtUtc;
            await _sessionRepository
                .SaveSessionsIndexAsync(workspaceId, sessions, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> SetSessionPinnedAsync(
        string workspaceId,
        string sessionId,
        bool isPinned,
        CancellationToken ct = default
    )
    {
        var meta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
            return false;

        meta.IsPinned = isPinned;
        meta.UpdatedAtUtc = DateTime.UtcNow;
        await _sessionRepository.SaveSessionMetaAsync(workspaceId, meta, ct).ConfigureAwait(false);

        var sessions = await _sessionRepository
            .LoadSessionsIndexAsync(workspaceId, ct)
            .ConfigureAwait(false);
        int idx = sessions.FindIndex(s =>
            string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx >= 0)
        {
            sessions[idx].IsPinned = isPinned;
            sessions[idx].UpdatedAtUtc = meta.UpdatedAtUtc;
            await _sessionRepository
                .SaveSessionsIndexAsync(workspaceId, sessions, ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    public Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.DeleteSessionAsync(workspaceId, sessionId, ct);
    }

    public Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _sessionRepository.ExportSessionMarkdownAsync(workspaceId, sessionId, ct);
    }

    /// <summary>need07：重放截断。持追加锁；切面以后端为准；失败不动盘。</summary>
    public async Task<HistoryTruncateResult> TruncateSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        string anchorMessageId,
        bool removeAnchor,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryTruncateResult.Fail("SESSION_NOT_FOUND");
        if (string.IsNullOrWhiteSpace(anchorMessageId))
            return HistoryTruncateResult.Fail("ANCHOR_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryTruncateResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            int idx = messages.FindIndex(m =>
                string.Equals(m.Id, anchorMessageId, StringComparison.OrdinalIgnoreCase)
            );
            if (idx < 0)
                return HistoryTruncateResult.Fail("ANCHOR_NOT_FOUND");
            string role = (messages[idx].Role ?? string.Empty).ToLowerInvariant();
            int cut;
            string? replayText;
            if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                int j = idx;
                while (
                    j >= 0
                    && !string.Equals(messages[j].Role, "user", StringComparison.OrdinalIgnoreCase)
                )
                    j--;
                if (j < 0)
                    return HistoryTruncateResult.Fail("NO_PRIOR_USER");
                cut = j;
                replayText = messages[j].Content;
            }
            else if (string.Equals(role, "user", StringComparison.Ordinal))
            {
                cut = idx;
                replayText = messages[idx].Content;
            }
            else
            {
                cut = removeAnchor ? idx : idx + 1;
                replayText = messages[idx].Content;
            }
            if (string.IsNullOrWhiteSpace(replayText))
                return HistoryTruncateResult.Fail("EMPTY_REPLAY_TEXT");
            var keptRaw = messages.Take(cut).ToList();
            int removed = messages.Count - keptRaw.Count;
            // FIX(tool-400)：截断切面可能劈开 assistant→tool 块，落盘前内存愈合，保证尾部配对完整。
            var (kept, synthetic, orphans) = RebuildHealedHistory(keptRaw);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.replay-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, kept, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryTruncateResult.Fail("REWRITE_ERROR");
            }
            meta.MessageCount = kept.Count;
            var lastSnap = kept.LastOrDefault(m =>
                m.InputTokens.HasValue && m.InputTokens.Value > 0
            );
            meta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            meta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] truncate ws={workspaceId} sess={sessionId} anchor={anchorMessageId} kept={kept.Count} removed={removed} synthetic={synthetic} orphans={orphans} bak={bak}"
                );
            }
            catch { }
            return HistoryTruncateResult.Ok(kept.Count, removed, replayText, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }
}
