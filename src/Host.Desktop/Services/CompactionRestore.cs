using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services.Context;

namespace Host.Desktop.Services;

/// <summary>P007：压缩备份恢复（运维操作，仅显式调用；全仓唯一允许删桩与全量重写处）。</summary>
public sealed class CompactionRestore
{
    private readonly WorkspaceSessionService _service;

    public CompactionRestore(WorkspaceSessionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>P007：按 09-§3 算法 1-9 执行；BACKUP_NOT_FOUND / NO_BACKUP / NO_STUB 零变化。</summary>
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
        string safeName = Path.GetFileName(backupFileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName))
            return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
        string bakPath = Path.Combine(
            _service.SessionRepository.GetSessionDirectory(workspaceId, sessionId),
            safeName
        );
        if (!File.Exists(bakPath))
            return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
        // 读恢复源：优先工作区库在线备份分区（与压缩备份同源），缺失时回退旧 jsonl 快照（过渡期）。
        var listA = new List<UnifiedChatMessage>();
        bool loadedFromWorkspaceBackup = false;
        try
        {
            string workspaceBakPath = _service.SessionRepository.Storage.GetWorkspaceBackupPath(
                workspaceId
            );
            if (File.Exists(workspaceBakPath))
            {
                var fromBak = ReadMessagesFromBackupLibrary(workspaceBakPath, sessionId);
                if (fromBak.Count > 0)
                {
                    listA.AddRange(fromBak);
                    loadedFromWorkspaceBackup = true;
                }
            }
        }
        catch { }
        if (!loadedFromWorkspaceBackup)
        {
            try
            {
                string[] lines = await File.ReadAllLinesAsync(bakPath, ct).ConfigureAwait(false);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<UnifiedChatMessage>(
                            line,
                            HaloJsonOptions.CompactCamelCase
                        );
                        if (m != null && !string.IsNullOrWhiteSpace(m.Id))
                            listA.Add(m);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return HistoryRestoreResult.Fail("BACKUP_NOT_FOUND");
            }
        }
        // P007 运维步骤 2：读当前全量。
        var listB =
            await _service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        // P007 运维步骤 4：缺失集 M（按 bak 序，HashSet 化）。
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
        // P007 运维步骤 5：幂等（缺失为空直接 NoOp，含已恢复后桩已删情形）。
        if (missing.Count == 0)
            return HistoryRestoreResult.NoOpResult(listB.Count);
        // P007 运维步骤 3：定位最后桩；无桩则 NO_STUB（零变化）。
        int stubIdx = ActiveContext.FindLastStubIndex(listB);
        if (stubIdx < 0)
            return HistoryRestoreResult.Fail("NO_STUB");
        var stub = listB[stubIdx];
        if (stub == null || string.IsNullOrWhiteSpace(stub.Id))
            return HistoryRestoreResult.Fail("NO_STUB");
        // P007 运维安全：桩 meta.backupFile=null（备份失败告警路径）不得重建。
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
        // P007 运维步骤 6：merged = 桩前 + 缺失（按 bak 序）+ 桩后；桩剔除。
        var merged = new List<UnifiedChatMessage>(listB.Count + missing.Count);
        for (int i = 0; i < stubIdx; i++)
            merged.Add(listB[i]);
        merged.AddRange(missing);
        for (int i = stubIdx + 1; i < listB.Count; i++)
            merged.Add(listB[i]);
        // P007 运维步骤 7：单事务写盘（jsonl 全量重写 + DB 全量重写 + 删桩轮）。
        var appendLock = _service.GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _service
                .SessionRepository.RewriteMessagesJsonlAsync(workspaceId, sessionId, merged, ct)
                .ConfigureAwait(false);
            try
            {
                string dbPath = _service.SessionRepository.Storage.GetWorkspaceDbPath(workspaceId);
                if (File.Exists(dbPath))
                {
                    var store = new SessionHistoryStore(
                        new SessionHistoryDb(_service.SessionRepository.Storage)
                    );
                    await store
                        .RewriteMessagesAsync(workspaceId, sessionId, merged, ct)
                        .ConfigureAwait(false);
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
            }
            catch
            {
                return HistoryRestoreResult.Fail("RESTORE_WRITE_ERROR");
            }
            // P007 运维步骤 8：meta 仅刷 MessageCount；CompactMode 保持；累计不动。
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
        // P007 运维步骤 9：返回恢复量。
        return HistoryRestoreResult.Ok(missing.Count, merged.Count);
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
