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
    /// <summary>need07：分支。前缀复制建新会话，原会话不动。</summary>
    public async Task<SessionMetadata> BranchSessionAsync(
        string workspaceId,
        string sourceSessionId,
        string anchorMessageId,
        bool includeAnchor,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sourceSessionId))
            throw new ArgumentException("Workspace ID 与源会话 ID 不能为空");
        var srcMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, sourceSessionId, ct)
            .ConfigureAwait(false);
        if (srcMeta == null)
            throw new InvalidOperationException("SESSION_NOT_FOUND");
        var messages = await _sessionRepository
            .GetMessagesAsync(workspaceId, sourceSessionId, ct)
            .ConfigureAwait(false);
        int idx = messages.FindIndex(m =>
            string.Equals(m.Id, anchorMessageId, StringComparison.OrdinalIgnoreCase)
        );
        if (idx < 0)
            throw new InvalidOperationException("ANCHOR_NOT_FOUND");
        int end = idx;
        if (string.Equals(messages[idx].Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            int t = idx + 1;
            while (
                t < messages.Count
                && string.Equals(messages[t].Role, "tool", StringComparison.OrdinalIgnoreCase)
            )
                t++;
            end = t - 1;
        }
        var prefixRaw = (includeAnchor ? messages.Take(end + 1) : messages.Take(idx)).ToList();
        // FIX(tool-400)：分支前缀同样可能劈开 assistant→tool 块，复制前内存愈合。
        var (prefix, _, _) = RebuildHealedHistory(prefixRaw);
        string title =
            string.IsNullOrWhiteSpace(srcMeta.Title) || srcMeta.Title == "新建会话"
                ? "新建会话"
                : srcMeta.Title + "·分支";
        // need13：分支永远是新 main，不继承父子链（CreateSessionAsync 委托 parent=null 即 main）
        var created = await CreateSessionAsync(
                workspaceId,
                title,
                srcMeta.ProviderId,
                srcMeta.ModelId,
                ct
            )
            .ConfigureAwait(false);
        var cloned = prefix
            .Select(m => new UnifiedChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ReasoningContent = m.ReasoningContent,
                ToolCalls = m.ToolCalls != null ? new List<UnifiedToolCall>(m.ToolCalls) : null,
                ToolCallId = m.ToolCallId,
                Name = m.Name,
                InputTokens = m.InputTokens,
                OutputTokens = m.OutputTokens,
                CachedTokens = m.CachedTokens,
                Images = m.Images != null ? new List<ChatImageRef>(m.Images) : null,
            })
            .ToList();
        await _sessionRepository
            .RewriteMessagesJsonlAsync(workspaceId, created.Id, cloned, ct)
            .ConfigureAwait(false);
        var newMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, created.Id, ct)
            .ConfigureAwait(false);
        if (newMeta != null)
        {
            newMeta.MessageCount = cloned.Count;
            var lastSnap = cloned.LastOrDefault(m =>
                m.InputTokens.HasValue && m.InputTokens.Value > 0
            );
            newMeta.TotalInputTokens = lastSnap?.InputTokens ?? 0;
            newMeta.TotalCachedTokens = lastSnap?.CachedTokens ?? 0;
            newMeta.CumulativePromptTokens = 0;
            newMeta.CumulativeCompletionTokens = 0;
            newMeta.UpdatedAtUtc = DateTime.UtcNow;
            await RefreshMetaIndexAsync(workspaceId, created.Id, newMeta, ct).ConfigureAwait(false);
            try
            {
                string dir = _sessionRepository.GetSessionDirectory(workspaceId, created.Id);
                System.IO.Directory.CreateDirectory(dir);
                string shortSrc =
                    sourceSessionId.Length > 8 ? sourceSessionId.Substring(0, 8) : sourceSessionId;
                string infoPath = System.IO.Path.Combine(
                    dir,
                    "messages.branch-src-" + shortSrc + ".json"
                );
                string infoJson = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        sourceSessionId,
                        anchorMessageId,
                        copiedAtUtc = DateTime.UtcNow,
                        copiedMessages = cloned.Count
                    }
                );
                await System
                    .IO.File.WriteAllTextAsync(infoPath, infoJson, ct)
                    .ConfigureAwait(false);
            }
            catch { }
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] branch ws={workspaceId} src={sourceSessionId} anchor={anchorMessageId} new={created.Id} copied={cloned.Count}"
                );
            }
            catch { }
            return newMeta;
        }
        return created;
    }

    /// <summary>synthetic cancelled tool 回包前缀（前端/导出可据此识别为中断补位，非真实工具输出）。</summary>
    public const string SyntheticCancelledToolPrefix = "[Tool Cancelled";

    public static string SyntheticCancelledToolText(string? functionName)
    {
        string name = string.IsNullOrWhiteSpace(functionName) ? "tool" : functionName!.Trim();
        return $"[Tool Cancelled - {name}]: 该工具调用在实际执行前被中断（用户手动停止或进程异常退出），未产生任何副作用。请基于已有上下文继续任务；如仍需要该操作，请重新发起工具调用。";
    }

    /// <summary>
    /// 纯函数：重建愈合后的历史。缺回包的 assistant tool_call 在其后原地补 synthetic cancelled tool；
    /// 找不到归属 assistant 的 tool 消息直接丢弃（否则下轮发模型必 400）。
    /// </summary>
    internal static (
        List<UnifiedChatMessage> Fixed,
        int Synthetic,
        int RemovedOrphans
    ) RebuildHealedHistory(IReadOnlyList<UnifiedChatMessage> messages)
    {
        var uncoveredByAssistant = new Dictionary<int, List<(string CallId, string? Name)>>();
        var orphanToolIndexes = new HashSet<int>();
        int n = messages?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            var m = messages![i];
            string role = (m.Role ?? string.Empty).ToLowerInvariant();
            if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                var calls = m
                    .ToolCalls?.Where(c => c != null && !string.IsNullOrWhiteSpace(c.CallId))
                    .ToList();
                if (calls == null || calls.Count == 0)
                    continue;
                var covered = new HashSet<string>(StringComparer.Ordinal);
                for (int j = i + 1; j < n; j++)
                {
                    var t = messages[j];
                    if (
                        !string.Equals(
                            (t.Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            StringComparison.Ordinal
                        )
                    )
                        break;
                    if (!string.IsNullOrWhiteSpace(t.ToolCallId))
                        covered.Add(t.ToolCallId!);
                }
                var uncovered = calls
                    .Where(c => !covered.Contains(c.CallId))
                    .Select(c => (c.CallId, (string?)c.FunctionName))
                    .ToList();
                if (uncovered.Count > 0)
                    uncoveredByAssistant[i] = uncovered;
            }
            else if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                // 回溯连续 tool 段找归属 assistant
                bool owned = false;
                if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                {
                    int k = i - 1;
                    while (
                        k >= 0
                        && string.Equals(
                            (messages[k].Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            StringComparison.Ordinal
                        )
                    )
                        k--;
                    if (
                        k >= 0
                        && string.Equals(
                            (messages[k].Role ?? string.Empty).ToLowerInvariant(),
                            "assistant",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        owned =
                            messages[k]
                                .ToolCalls?.Any(c =>
                                    c != null
                                    && string.Equals(
                                        c.CallId,
                                        m.ToolCallId,
                                        StringComparison.Ordinal
                                    )
                                ) == true;
                    }
                }
                if (!owned)
                    orphanToolIndexes.Add(i);
            }
        }
        if (uncoveredByAssistant.Count == 0 && orphanToolIndexes.Count == 0)
        {
            return (messages!.ToList(), 0, 0);
        }
        // FIX(tool-400)：合成一律追加在存量 tool 块尾（[assistant, toolA, syntheticB]），不插在块中，避免严格网关误杀。
        var fixed_ = new List<UnifiedChatMessage>(
            n + uncoveredByAssistant.Values.Sum(v => v.Count)
        );
        int synthetic = 0;
        int idx = 0;
        while (idx < n)
        {
            if (orphanToolIndexes.Contains(idx))
            {
                idx++;
                continue;
            }
            fixed_.Add(messages![idx]);
            if (uncoveredByAssistant.TryGetValue(idx, out var uncovered))
            {
                int j = idx + 1;
                while (
                    j < n
                    && string.Equals(
                        (messages[j].Role ?? string.Empty).ToLowerInvariant(),
                        "tool",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    if (orphanToolIndexes.Contains(j))
                    {
                        j++;
                        continue;
                    }
                    fixed_.Add(messages[j]);
                    j++;
                }
                foreach (var (callId, name) in uncovered)
                {
                    fixed_.Add(
                        UnifiedChatMessage.Tool(callId, SyntheticCancelledToolText(name), name)
                    );
                    synthetic++;
                }
                idx = j;
            }
            else
            {
                idx++;
            }
        }
        return (fixed_, synthetic, orphanToolIndexes.Count);
    }

    /// <summary>孤儿愈合落盘（含备份 + meta 计数修正）。无损伤时只读不写。</summary>
    public async Task<HistoryHealResult> HealSessionHistoryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryHealResult.Fail("SESSION_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryHealResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            var (fixed_, synthetic, removed) = RebuildHealedHistory(messages);
            if (synthetic == 0 && removed == 0)
                return HistoryHealResult.Clean(messages.Count);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.heal-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryHealResult.Fail("REWRITE_ERROR");
            }
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
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] heal ws={workspaceId} sess={sessionId} synthetic={synthetic} removedOrphans={removed} kept={fixed_.Count} bak={bak}"
                );
            }
            catch { }
            return HistoryHealResult.Ok(synthetic, removed, fixed_.Count, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>删除单条消息（先备份 → 删行 → 同一次重写内愈合，失败不动盘）。</summary>
    public async Task<HistoryDeleteResult> DeleteSessionMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryDeleteResult.Fail("SESSION_NOT_FOUND");
        if (string.IsNullOrWhiteSpace(messageId))
            return HistoryDeleteResult.Fail("MESSAGE_NOT_FOUND");
        var appendLock = GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = await _sessionRepository
                .LoadSessionMetaAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (meta == null)
                return HistoryDeleteResult.Fail("SESSION_NOT_FOUND");
            var messages = await _sessionRepository
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            int idx = messages.FindIndex(m =>
                string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase)
            );
            if (idx < 0)
                return HistoryDeleteResult.Fail("MESSAGE_NOT_FOUND");
            var kept = messages.Where((_, i) => i != idx).ToList();
            var (fixed_, synthetic, removed) = RebuildHealedHistory(kept);
            string bak = await _sessionRepository
                .BackupMessagesJsonlAsync(workspaceId, sessionId, "messages.delete-bak-", ct)
                .ConfigureAwait(false);
            try
            {
                await _sessionRepository
                    .RewriteMessagesJsonlAsync(workspaceId, sessionId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryDeleteResult.Fail("REWRITE_ERROR");
            }
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
            try
            {
                Infrastructure.Logging.HaloLogger.App.Info(
                    $"[History] delete ws={workspaceId} sess={sessionId} msg={messageId} kept={fixed_.Count} synthetic={synthetic} removedOrphans={removed} bak={bak}"
                );
            }
            catch { }
            return HistoryDeleteResult.Ok(fixed_.Count, synthetic, removed, bak);
        }
        finally
        {
            appendLock.Release();
        }
    }
}
