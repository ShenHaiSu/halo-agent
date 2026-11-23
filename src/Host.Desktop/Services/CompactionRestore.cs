using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services.Context;

namespace Host.Desktop.Services;

/// <summary>压缩备份恢复（运维操作，仅显式调用；全仓唯一允许删桩与全量重写处）。</summary>
public sealed class CompactionRestore
{
    private readonly WorkspaceSessionService _service;

    public CompactionRestore(WorkspaceSessionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>恢复算法：备份分区读回缺失消息，剔除压缩桩后写回；缺备份或无桩时零变化。</summary>
    public async Task<HistoryRestoreResult> RestoreAsync(
        string workspaceId,
        string sessionId,
        string backupFileName,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return HistoryRestoreResult.Fail("INVALID_SESSION");
        if (string.IsNullOrWhiteSpace(backupFileName))
            return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
        // 备份固定为工作区库快照文件；传入旧文件名时忽略，直接使用工作区快照路径。
        string bakPath = _service.SessionRepository.Storage.GetWorkspaceBackupPath(workspaceId);
        if (!File.Exists(bakPath))
            return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
        // 恢复源：工作区快照库中本会话的消息分区。
        var listA = ReadMessagesFromBackupLibrary(bakPath, sessionId);
        if (listA.Count == 0)
            return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
        // 读当前全量。
        var listB =
            await _service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        // 缺失集（按备份序）。
        var currentIds = new HashSet<string>(
            listB.Where(m => m != null).Select(m => m.Id),
            StringComparer.Ordinal
        );
        var missing = new List<UnifiedChatMessage>();
        foreach (var m in listA)
        {
            if (!currentIds.Contains(m.Id))
                missing.Add(m);
        }
        // 幂等（缺失为空直接 NoOp，含已恢复后桩已删情形）。
        if (missing.Count == 0)
            return HistoryRestoreResult.NoOpResult(listB.Count);
        // 定位最后桩；无桩则 NO_STUB（零变化）。
        int stubIdx = ActiveContext.FindLastStubIndex(listB);
        if (stubIdx < 0)
            return HistoryRestoreResult.Fail("NO_STUB");
        var stub = listB[stubIdx];
        if (stub == null || string.IsNullOrWhiteSpace(stub.Id))
            return HistoryRestoreResult.Fail("NO_STUB");
        // 桩无备份引用不得重建。
        string? backupRef = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stub.MetaJson))
                backupRef = JsonSerializer
                    .Deserialize<CompactStubMeta>(stub.MetaJson, HaloJsonOptions.CompactCamelCase)
                    ?.BackupFile;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(backupRef))
            return HistoryRestoreResult.Fail("NO_BACKUP");
        // merged = 桩前 + 缺失（按备份序）+ 桩后；桩剔除。
        var merged = new List<UnifiedChatMessage>(listB.Count + missing.Count);
        for (int i = 0; i < stubIdx; i++)
            merged.Add(listB[i]);
        merged.AddRange(missing);
        for (int i = stubIdx + 1; i < listB.Count; i++)
            merged.Add(listB[i]);
        // 单事务写盘（消息分区重写 + 展示块与轮次分区补齐 + 删桩轮）。
        var appendLock = _service.GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string dbPath = _service.SessionRepository.Storage.GetWorkspaceDbPath(workspaceId);
            try
            {
                var store = new SessionHistoryStore(
                    new SessionHistoryDb(_service.SessionRepository.Storage)
                );
                await store
                    .RewriteMessagesAsync(workspaceId, sessionId, merged, ct)
                    .ConfigureAwait(false);
                RestoreSessionBlocksAndTurns(dbPath, bakPath, sessionId);
                string stubTurnId = "cturn-" + stub.Id;
                try
                {
                    using var conn = SessionHistoryDb.OpenConnection(dbPath);
                    using var delB = conn.CreateCommand();
                    delB.CommandText =
                        "DELETE FROM blocks WHERE session_id = $sid AND turn_id = $tid;";
                    delB.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                    delB.Parameters.AddWithValue("$tid", stubTurnId);
                    delB.ExecuteNonQuery();
                    using var delT = conn.CreateCommand();
                    delT.CommandText =
                        "DELETE FROM turns WHERE session_id = $sid AND turn_id = $tid;";
                    delT.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                    delT.Parameters.AddWithValue("$tid", stubTurnId);
                    delT.ExecuteNonQuery();
                }
                catch { }
            }
            catch
            {
                return HistoryRestoreResult.Fail("RESTORE_WRITE_ERROR");
            }
            // meta 仅刷消息条数；压缩模式保持；累计不动。
            try
            {
                var meta = await _service
                    .SessionRepository.LoadSessionMetaAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
                if (meta != null)
                {
                    meta.MessageCount = merged.Count;
                    meta.UpdatedAtUtc = DateTime.UtcNow;
                    await _service
                        .RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct)
                        .ConfigureAwait(false);
                }
            }
            catch { }
        }
        finally
        {
            appendLock.Release();
        }
        // 返回恢复量。
        return HistoryRestoreResult.Ok(missing.Count, merged.Count);
    }

    /// <summary>按会话分区从快照库补回缺失的展示块与轮次（仅本会话，不整库替换）。</summary>
    private static void RestoreSessionBlocksAndTurns(
        string currentDbPath,
        string backupDbPath,
        string sessionId
    )
    {
        string escaped = (backupDbPath ?? string.Empty).Replace("'", "''");
        using var conn = SessionHistoryDb.OpenConnection(currentDbPath);
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH '" + escaped + "' AS restore_bak;";
            attach.ExecuteNonQuery();
        }
        try
        {
            using var tx = conn.BeginTransaction();
            using (var copyBlocks = conn.CreateCommand())
            {
                copyBlocks.Transaction = tx;
                copyBlocks.CommandText =
                    "INSERT OR IGNORE INTO blocks SELECT * FROM restore_bak.blocks WHERE session_id = $sid;";
                copyBlocks.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                copyBlocks.ExecuteNonQuery();
            }
            using (var copyTurns = conn.CreateCommand())
            {
                copyTurns.Transaction = tx;
                copyTurns.CommandText =
                    "INSERT OR IGNORE INTO turns SELECT * FROM restore_bak.turns WHERE session_id = $sid;";
                copyTurns.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                copyTurns.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH restore_bak;";
            detach.ExecuteNonQuery();
        }
    }

    private static List<UnifiedChatMessage> ReadMessagesFromBackupLibrary(
        string backupDbPath,
        string sessionId
    )
    {
        var result = new List<UnifiedChatMessage>();
        try
        {
            using var conn = SessionHistoryDb.OpenConnection(backupDbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, kind, meta_json, created_utc"
                + " FROM messages WHERE session_id = $sid ORDER BY seq;";
            cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    string id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var msg = new UnifiedChatMessage { Id = id };
                    msg.Role = reader.IsDBNull(1) ? "assistant" : reader.GetString(1);
                    msg.Content = reader.IsDBNull(2) ? null : reader.GetString(2);
                    msg.ReasoningContent = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!reader.IsDBNull(4))
                    {
                        try
                        {
                            msg.ToolCalls = JsonSerializer.Deserialize<
                                List<LlmGateway.Models.UnifiedToolCall>
                            >(reader.GetString(4), HaloJsonOptions.CompactCamelCase);
                        }
                        catch { }
                    }
                    if (!reader.IsDBNull(5))
                        msg.ToolCallId = reader.GetString(5);
                    if (!reader.IsDBNull(6))
                        msg.Name = reader.GetString(6);
                    if (!reader.IsDBNull(8))
                        msg.InputTokens = reader.GetInt64(8);
                    if (!reader.IsDBNull(9))
                        msg.OutputTokens = reader.GetInt64(9);
                    if (!reader.IsDBNull(10))
                        msg.CachedTokens = reader.GetInt64(10);
                    if (!reader.IsDBNull(11))
                        msg.Kind = reader.GetString(11);
                    if (!reader.IsDBNull(12))
                        msg.MetaJson = reader.GetString(12);
                    result.Add(msg);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }
}
