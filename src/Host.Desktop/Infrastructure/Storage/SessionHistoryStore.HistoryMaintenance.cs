using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>历史维护写操作：整轮删除在单事务内覆盖 messages/turns/blocks。</summary>
public sealed partial class SessionHistoryStore
{
    /// <summary>
    /// 删除一轮的 messages、turns、blocks。四个动作共享同一事务；任一 SQL 失败都抛出，
    /// 由上层映射为未提交。不重排其他轮次的 turn_seq，不删除 sessions 注册行。
    /// </summary>
    public async Task DeleteTurnAndRewriteMessagesAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        IReadOnlyList<UnifiedChatMessage> keptMessages,
        CancellationToken ct = default
    )
    {
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureWorkspaceDatabase(workspaceId);
            using var conn = _db.OpenWorkspace(workspaceId);
            using var tx = conn.BeginTransaction();

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM messages WHERE session_id = $sid;";
                del.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                del.ExecuteNonQuery();
            }
            InsertMessages(conn, tx, sessionId!, keptMessages);

            using (var delBlocks = conn.CreateCommand())
            {
                delBlocks.Transaction = tx;
                delBlocks.CommandText =
                    "DELETE FROM blocks WHERE session_id = $sid AND turn_id = $tid;";
                delBlocks.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                delBlocks.Parameters.AddWithValue("$tid", turnId ?? string.Empty);
                delBlocks.ExecuteNonQuery();
            }
            using (var delTurns = conn.CreateCommand())
            {
                delTurns.Transaction = tx;
                delTurns.CommandText =
                    "DELETE FROM turns WHERE session_id = $sid AND turn_id = $tid;";
                delTurns.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                delTurns.Parameters.AddWithValue("$tid", turnId ?? string.Empty);
                delTurns.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            gate.Release();
        }
    }
}
