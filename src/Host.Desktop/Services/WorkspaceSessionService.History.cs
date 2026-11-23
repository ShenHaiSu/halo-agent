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
    /// <summary>分支：以前缀复制建新会话，原会话不动。</summary>
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
        // 分支前缀同样可能劈开问答与工具回包的组合，复制前先在内存里愈合补齐。
        var (prefix, _, _) = RebuildHealedHistory(prefixRaw);
        string title =
            string.IsNullOrWhiteSpace(srcMeta.Title) || srcMeta.Title == "新建会话"
                ? "新建会话"
                : srcMeta.Title + "·分支";
        // 分支永远建为主会话，不继承父子链。
        var created = await CreateSessionAsync(
                workspaceId,
                title,
                srcMeta.ProviderId,
                srcMeta.ModelId,
                ct
            )
            .ConfigureAwait(false);
        var branchStore = new SessionHistoryStore(new SessionHistoryDb(_sessionRepository.Storage));
        // 库内分区复制：消息可按原标识共存，多轮与文本块复制时重映射全局标识，避免搬走源分区；落盘的超长工具输出随复制同步搬运。
        try
        {
            RemapCopyPartitionForBranch(
                _sessionRepository.Storage,
                workspaceId,
                sourceSessionId,
                created.Id
            );
        }
        catch { }
        await branchStore
            .RewriteMessagesAsync(workspaceId, created.Id, prefix, ct)
            .ConfigureAwait(false);
        // 复制后按前缀截断目标库后缀轮次，只留分支锚点之前的内容。
        try
        {
            var keptIds = new HashSet<string>(prefix.Select(m => m.Id), StringComparer.Ordinal);
            var dstSummaries = await branchStore
                .GetTimelineSummaryAsync(workspaceId, created.Id, ct)
                .ConfigureAwait(false);
            long minDoomed = long.MaxValue;
            foreach (var s in dstSummaries)
            {
                string tid = s.TurnId ?? string.Empty;
                const string turnPrefix = "uturn-";
                string suffix = tid.StartsWith(turnPrefix, StringComparison.Ordinal)
                    ? tid.Substring(turnPrefix.Length)
                    : tid;
                int branchSep = suffix.IndexOf(":branch:", StringComparison.Ordinal);
                if (branchSep >= 0)
                    suffix = suffix.Substring(0, branchSep);
                if (!keptIds.Contains(suffix) && s.TurnSeq < minDoomed)
                    minDoomed = s.TurnSeq;
            }
            if (minDoomed != long.MaxValue)
                await branchStore
                    .TruncateFromTurnSeqAsync(workspaceId, created.Id, minDoomed, ct)
                    .ConfigureAwait(false);
            try
            {
                await branchStore
                    .CollectOrphanToolOutputsAsync(workspaceId, created.Id, ct)
                    .ConfigureAwait(false);
            }
            catch { }
        }
        catch { }
        var newMeta = await _sessionRepository
            .LoadSessionMetaAsync(workspaceId, created.Id, ct)
            .ConfigureAwait(false);
        if (newMeta != null)
        {
            newMeta.MessageCount = prefix.Count;
            var lastSnap = prefix.LastOrDefault(m =>
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
                        copiedMessages = prefix.Count
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
                    $"[History] branch ws={workspaceId} src={sourceSessionId} anchor={anchorMessageId} new={created.Id} copied={prefix.Count}"
                );
            }
            catch { }
            return newMeta;
        }
        return created;
    }

    /// <summary>分支复制：把源会话的多轮与文本块复制到目标分区并重映射全局标识，同时搬运落盘的超长工具输出。</summary>
    private static void RemapCopyPartitionForBranch(
        StoragePathManager storage,
        string workspaceId,
        string sourceSessionId,
        string targetSessionId
    )
    {
        var db = new SessionHistoryDb(storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        string marker = ":branch:" + (targetSessionId ?? string.Empty);
        var turnIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var blockIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentRefs = new List<string>();
        var turnRows = new List<object?[]>();
        var blockRows = new List<object?[]>();
        using (var conn = db.OpenWorkspace(workspaceId))
        {
            using (var q = conn.CreateCommand())
            {
                q.CommandText =
                    "SELECT turn_id, turn_seq, iter_end, user_preview, user_content,"
                    + " final_block_id, blocks, tools, failed, duration_ms, chars, status,"
                    + " last_request_id, kind, removed_messages, updated_at"
                    + " FROM turns WHERE session_id = $sid ORDER BY turn_seq;";
                q.Parameters.AddWithValue("$sid", sourceSessionId ?? string.Empty);
                using var reader = q.ExecuteReader();
                while (reader.Read())
                {
                    string oldTurnId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(oldTurnId))
                        continue;
                    turnIdMap[oldTurnId] = oldTurnId + marker;
                    var row = new object?[16];
                    for (int c = 0; c < 16; c++)
                        row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                    turnRows.Add(row);
                }
            }
            using (var q = conn.CreateCommand())
            {
                q.CommandText =
                    "SELECT block_id, turn_id, iter, seq, kind, tag, call_id, function_name,"
                    + " success, char_count, preview, content, content_ref, truncated"
                    + " FROM blocks WHERE session_id = $sid ORDER BY seq;";
                q.Parameters.AddWithValue("$sid", sourceSessionId ?? string.Empty);
                using var reader = q.ExecuteReader();
                while (reader.Read())
                {
                    string oldBlockId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(oldBlockId))
                        continue;
                    blockIdMap[oldBlockId] = oldBlockId + marker;
                    if (!reader.IsDBNull(12))
                    {
                        string contentRef = reader.GetString(12);
                        if (!string.IsNullOrWhiteSpace(contentRef))
                            contentRefs.Add(contentRef);
                    }
                    var row = new object?[14];
                    for (int c = 0; c < 14; c++)
                        row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                    blockRows.Add(row);
                }
            }
            using var tx = conn.BeginTransaction();
            foreach (var row in turnRows)
            {
                string oldTurnId = (row[0] as string) ?? string.Empty;
                if (!turnIdMap.TryGetValue(oldTurnId, out string? newTurnId))
                    continue;
                string finalBlockId = (row[5] as string) ?? string.Empty;
                if (
                    !string.IsNullOrEmpty(finalBlockId)
                    && blockIdMap.TryGetValue(finalBlockId, out string? mappedFinal)
                )
                    finalBlockId = mappedFinal;
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT OR IGNORE INTO turns(session_id, turn_id, turn_seq, iter_end,"
                    + " user_preview, user_content, final_block_id, blocks, tools, failed,"
                    + " duration_ms, chars, status, last_request_id, kind, removed_messages,"
                    + " updated_at) VALUES($sid, $tid, $seq, $iterEnd, $userPreview,"
                    + " $userContent, $finalBid, $blocks, $tools, $failed, $dur, $chars,"
                    + " $status, $req, $kind, $removed, $updated);";
                ins.Parameters.AddWithValue("$sid", targetSessionId ?? string.Empty);
                ins.Parameters.AddWithValue("$tid", newTurnId);
                AddCopyParam(ins, "$seq", row[1]);
                AddCopyParam(ins, "$iterEnd", row[2]);
                AddCopyParam(ins, "$userPreview", row[3]);
                AddCopyParam(ins, "$userContent", row[4]);
                ins.Parameters.AddWithValue(
                    "$finalBid",
                    string.IsNullOrEmpty(finalBlockId) ? (object)DBNull.Value : finalBlockId
                );
                AddCopyParam(ins, "$blocks", row[6]);
                AddCopyParam(ins, "$tools", row[7]);
                AddCopyParam(ins, "$failed", row[8]);
                AddCopyParam(ins, "$dur", row[9]);
                AddCopyParam(ins, "$chars", row[10]);
                AddCopyParam(ins, "$status", row[11]);
                AddCopyParam(ins, "$req", row[12]);
                AddCopyParam(ins, "$kind", row[13]);
                AddCopyParam(ins, "$removed", row[14]);
                AddCopyParam(ins, "$updated", row[15]);
                ins.ExecuteNonQuery();
            }
            foreach (var row in blockRows)
            {
                string oldBlockId = (row[0] as string) ?? string.Empty;
                if (!blockIdMap.TryGetValue(oldBlockId, out string? newBlockId))
                    continue;
                string oldTurnId = (row[1] as string) ?? string.Empty;
                if (!turnIdMap.TryGetValue(oldTurnId, out string? newTurnId))
                    newTurnId = oldTurnId + marker;
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT OR IGNORE INTO blocks(session_id, block_id, turn_id, iter, seq,"
                    + " kind, tag, call_id, function_name, success, char_count, preview,"
                    + " content, content_ref, truncated)"
                    + " VALUES($sid, $bid, $tid, $iter, $seq, $kind, $tag, $call, $fn,"
                    + " $success, $chars, $preview, $content, $ref, $trunc);";
                ins.Parameters.AddWithValue("$sid", targetSessionId ?? string.Empty);
                ins.Parameters.AddWithValue("$bid", newBlockId);
                ins.Parameters.AddWithValue("$tid", newTurnId);
                AddCopyParam(ins, "$iter", row[2]);
                AddCopyParam(ins, "$seq", row[3]);
                AddCopyParam(ins, "$kind", row[4]);
                AddCopyParam(ins, "$tag", row[5]);
                AddCopyParam(ins, "$call", row[6]);
                AddCopyParam(ins, "$fn", row[7]);
                AddCopyParam(ins, "$success", row[8]);
                AddCopyParam(ins, "$chars", row[9]);
                AddCopyParam(ins, "$preview", row[10]);
                AddCopyParam(ins, "$content", row[11]);
                AddCopyParam(ins, "$ref", row[12]);
                AddCopyParam(ins, "$trunc", row[13]);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        CopyBranchToolOutputFiles(
            storage,
            workspaceId,
            sourceSessionId!,
            targetSessionId!,
            contentRefs
        );
    }

    private static void AddCopyParam(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        string name,
        object? value
    )
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    /// <summary>分支复制落盘文件：按引用解析文件名，从源会话输出目录复制到目标会话输出目录。</summary>
    private static void CopyBranchToolOutputFiles(
        StoragePathManager storage,
        string workspaceId,
        string sourceSessionId,
        string targetSessionId,
        List<string> contentRefs
    )
    {
        try
        {
            string srcDir = storage.GetSessionDirectory(workspaceId, sourceSessionId);
            string srcOutputs = storage.GetSessionToolOutputsDir(workspaceId, sourceSessionId);
            string dstOutputs = storage.GetSessionToolOutputsDir(workspaceId, targetSessionId);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string contentRef in contentRefs)
            {
                string? fileName = ExtractToolOutputFileName(contentRef);
                if (!string.IsNullOrWhiteSpace(fileName))
                    fileNames.Add(fileName!);
            }
            if (fileNames.Count == 0)
                return;
            System.IO.Directory.CreateDirectory(dstOutputs);
            foreach (string fileName in fileNames)
            {
                try
                {
                    string srcFile = System.IO.Path.Combine(srcOutputs, fileName);
                    if (!System.IO.File.Exists(srcFile))
                    {
                        string alt = System.IO.Path.Combine(srcDir, "tool-outputs", fileName);
                        if (System.IO.File.Exists(alt))
                            srcFile = alt;
                    }
                    if (!System.IO.File.Exists(srcFile))
                        continue;
                    string dstFile = System.IO.Path.Combine(dstOutputs, fileName);
                    System.IO.File.Copy(srcFile, dstFile, overwrite: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? ExtractToolOutputFileName(string? contentRef)
    {
        if (string.IsNullOrWhiteSpace(contentRef))
            return null;
        string rel = contentRef.Trim();
        const string fileRefPrefix = "fileRef:";
        if (rel.StartsWith(fileRefPrefix, StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(fileRefPrefix.Length).Trim().Trim('"');
        int sp = rel.IndexOf(" size=", StringComparison.OrdinalIgnoreCase);
        if (sp > 0)
            rel = rel.Substring(0, sp).Trim().Trim('"');
        rel = rel.Replace('\\', '/').Trim();
        string fileName = System.IO.Path.GetFileName(rel);
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
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
            // 库完整性校验：损坏只打点，不阻断愈合写入。
            try
            {
                var integrityStore = new SessionHistoryStore(
                    new SessionHistoryDb(_sessionRepository.Storage)
                );
                var (ok, detail) = await integrityStore
                    .CheckIntegrityAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    try
                    {
                        Infrastructure.Logging.HaloLogger.App.Warn(
                            $"[History] integrity fail ws={workspaceId} sess={sessionId} detail={detail}"
                        );
                    }
                    catch { }
                }
            }
            catch { }
            if (synthetic == 0 && removed == 0)
                return HistoryHealResult.Clean(messages.Count);
            var workspaceStore = new WorkspaceSessionStore(_sessionRepository.Storage);
            string bak = (
                await workspaceStore.BackupWorkspaceAsync(workspaceId, ct).ConfigureAwait(false)
            ).BackupPath;
            try
            {
                var healStore = new SessionHistoryStore(
                    new SessionHistoryDb(_sessionRepository.Storage)
                );
                await healStore
                    .RewriteMessagesAsync(workspaceId, sessionId, fixed_, ct)
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
            // 先做工作区库在线快照，快照路径随结果返回。
            var deleteStore = new SessionHistoryStore(
                new SessionHistoryDb(_sessionRepository.Storage)
            );
            var deleteWorkspaceStore = new WorkspaceSessionStore(_sessionRepository.Storage);
            string bak = (
                await deleteWorkspaceStore
                    .BackupWorkspaceAsync(workspaceId, ct)
                    .ConfigureAwait(false)
            ).BackupPath;
            try
            {
                await deleteStore
                    .RewriteMessagesAsync(workspaceId, sessionId, fixed_, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return HistoryDeleteResult.Fail("REWRITE_ERROR");
            }
            // 同步多轮与文本块：删的是用户轮首条则连带截断该轮，否则只删单行，最后清理孤儿落盘文件。
            try
            {
                var doomed = messages[idx];
                if (string.Equals(doomed.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var summaries = await deleteStore
                            .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                            .ConfigureAwait(false);
                        var hit = summaries.FirstOrDefault(s =>
                            string.Equals(s.TurnId, "uturn-" + doomed.Id, StringComparison.Ordinal)
                        );
                        if (hit != null)
                            await deleteStore
                                .TruncateFromTurnSeqAsync(workspaceId, sessionId, hit.TurnSeq, ct)
                                .ConfigureAwait(false);
                    }
                    catch { }
                }
                else
                {
                    await deleteStore
                        .DeleteMessageRowAsync(workspaceId, sessionId, messageId, ct)
                        .ConfigureAwait(false);
                }
                try
                {
                    await deleteStore
                        .CollectOrphanToolOutputsAsync(workspaceId, sessionId, ct)
                        .ConfigureAwait(false);
                }
                catch { }
            }
            catch { }
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
