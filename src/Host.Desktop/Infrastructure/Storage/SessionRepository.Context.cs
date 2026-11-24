using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>活跃切片读的仓储门面（工作区单库单语句快路径）。
/// 仓储层保持简单：只做查询与回退，不做业务判断；切片语义由服务层定义。</summary>
public sealed partial class SessionRepository : ISessionRepository
{
    private const string ActiveContextColumns =
        "SELECT id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, kind, meta_json, created_utc";

    /// <summary>
    /// 活跃切片读。单语句返回前导系统消息与桩后消息，无桩时等价全量；
    /// 无库或失败时回退全量读。单语句避免压缩并发竞态。
    /// </summary>
    public async Task<List<UnifiedChatMessage>> GetActiveContextMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            string dbPath = _storageManager.GetWorkspaceDbPath(workspaceId);
            if (File.Exists(dbPath))
            {
                var db = new SessionHistoryDb(_storageManager);
                db.EnsureWorkspaceDatabase(workspaceId);
                using var conn = db.OpenWorkspace(workspaceId);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    ActiveContextColumns
                    + " FROM messages WHERE session_id = $sid AND ((SELECT MAX(seq) FROM messages WHERE session_id = $sid AND kind='compact_stub') IS NULL"
                    + " OR seq >= (SELECT MAX(seq) FROM messages WHERE session_id = $sid AND kind='compact_stub')"
                    + " OR (role='system' AND seq < (SELECT MIN(seq) FROM messages WHERE session_id = $sid AND role<>'system'))"
                    + ") ORDER BY seq;";
                cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                var result = new List<UnifiedChatMessage>();
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var msg = SessionHistoryStore.MapCompactMessage(reader);
                        if (msg != null)
                            result.Add(msg);
                    }
                    catch { }
                }
                return result;
            }
        }
        catch { }
        // 回退：全量读，由服务层做内存切片。
        return await GetMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
    }
}
