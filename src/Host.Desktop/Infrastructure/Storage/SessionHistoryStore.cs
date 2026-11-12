using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// Per-session timeline write and summary read.
/// Single writer per session, short-lived connections, full single-transaction turn commit.
/// </summary>
public sealed class SessionHistoryStore
{
    private readonly SessionHistoryDb _db;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    public SessionHistoryStore(SessionHistoryDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public SessionHistoryStore(StoragePathManager storage)
        : this(new SessionHistoryDb(storage)) { }

    private SemaphoreSlim GetWriteLock(string workspaceId, string sessionId)
    {
        string key =
            (workspaceId ?? string.Empty).Trim() + "/" + (sessionId ?? string.Empty).Trim();
        return _writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<long> AppendTurnAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> messages,
        IReadOnlyList<TimelineBlock> blocks,
        TurnSummary turn,
        CancellationToken ct = default
    )
    {
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            long turnSeq = ResolveTurnSeq(conn, tx, turn);
            InsertMessages(conn, tx, messages);
            InsertBlocks(conn, tx, blocks);
            UpsertTurn(conn, tx, turn, turnSeq);
            tx.Commit();
            watch.Stop();
            try
            {
                HaloLogger.App.Info(
                    "[Timeline] AppendTurn ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " turn="
                        + turn.TurnId
                        + " blocks="
                        + (blocks == null ? 0 : blocks.Count)
                        + " flushMs="
                        + watch.ElapsedMilliseconds
                );
            }
            catch { }
            return watch.ElapsedMilliseconds;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<List<TurnSummary>> GetTimelineSummaryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        _db.EnsureDatabase(workspaceId, sessionId);
        var result = new List<TurnSummary>();
        using var conn = _db.Open(workspaceId, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT turn_id, turn_seq, iter_end, user_preview, final_block_id,"
            + " (SELECT preview FROM blocks WHERE block_id = turns.final_block_id) AS final_preview,"
            + " blocks, tools, failed, duration_ms, chars, status, last_request_id, updated_at"
            + " FROM turns ORDER BY turn_seq;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string turnId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var summary = new TurnSummary
            {
                TurnId = turnId,
                TurnSeq = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                IterEnd = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                UserPreview = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                FinalBlockId = reader.IsDBNull(4) ? null : reader.GetString(4),
                FinalPreview = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Blocks = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Tools = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                Failed = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                DurationMs = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                Chars = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                Status = reader.IsDBNull(11) ? TimelineTurnStatus.Completed : reader.GetString(11),
                LastRequestId = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                UpdatedAtUtc = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            };
            summary.CreatedAtUtc = summary.UpdatedAtUtc;
            summary.AnchorKey = turnId;
            result.Add(summary);
        }
        return result;
    }

    private static long ResolveTurnSeq(
        SqliteConnection conn,
        SqliteTransaction tx,
        TurnSummary turn
    )
    {
        if (!string.IsNullOrWhiteSpace(turn.TurnId))
        {
            using var exists = conn.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = "SELECT turn_seq FROM turns WHERE turn_id = $id LIMIT 1;";
            exists.Parameters.AddWithValue("$id", turn.TurnId);
            object? hit = exists.ExecuteScalar();
            if (hit != null && hit != DBNull.Value)
            {
                return Convert.ToInt64(hit);
            }
        }
        using var max = conn.CreateCommand();
        max.Transaction = tx;
        max.CommandText = "SELECT COALESCE(MAX(turn_seq), 0) FROM turns;";
        object? current = max.ExecuteScalar();
        long baseSeq = current == null || current == DBNull.Value ? 0 : Convert.ToInt64(current);
        if (turn.TurnSeq > baseSeq)
        {
            return turn.TurnSeq;
        }
        return baseSeq + 1;
    }

    private static void InsertMessages(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<UnifiedChatMessage>? messages
    )
    {
        if (messages == null || messages.Count == 0)
        {
            return;
        }
        foreach (var msg in messages)
        {
            if (msg == null || string.IsNullOrWhiteSpace(msg.Id))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR IGNORE INTO messages(id, role, content, reasoning, tool_calls_json, input_tokens, output_tokens, cached_tokens, created_utc)"
                + " VALUES($id, $role, $content, $reasoning, $tools, $inTok, $outTok, $cached, $utc);";
            cmd.Parameters.AddWithValue("$id", msg.Id);
            cmd.Parameters.AddWithValue("$role", msg.Role ?? "assistant");
            cmd.Parameters.AddWithValue("$content", (object?)msg.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$reasoning",
                (object?)msg.ReasoningContent ?? DBNull.Value
            );
            string? toolsJson = null;
            try
            {
                if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    toolsJson = JsonSerializer.Serialize(
                        msg.ToolCalls,
                        HaloJsonOptions.CompactCamelCase
                    );
                }
            }
            catch { }
            cmd.Parameters.AddWithValue("$tools", (object?)toolsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inTok", (object?)msg.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outTok", (object?)msg.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cached", (object?)msg.CachedTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$utc", msg.CreatedAtUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertBlocks(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<TimelineBlock>? blocks
    )
    {
        if (blocks == null || blocks.Count == 0)
        {
            return;
        }
        foreach (var block in blocks)
        {
            if (block == null || string.IsNullOrWhiteSpace(block.BlockId))
            {
                continue;
            }
            string tag = block.Tag ?? TimelineBlockTag.Process;
            if (string.Equals(tag, TimelineBlockTag.Pending, StringComparison.OrdinalIgnoreCase))
            {
                tag = TimelineBlockTag.Process;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, char_count, preview, content, content_ref, truncated)"
                + " VALUES($bid, $tid, $iter, $seq, $kind, $tag, $call, $chars, $preview, $content, $ref, $trunc);";
            cmd.Parameters.AddWithValue("$bid", block.BlockId);
            cmd.Parameters.AddWithValue("$tid", block.TurnId ?? string.Empty);
            cmd.Parameters.AddWithValue("$iter", block.Iteration);
            cmd.Parameters.AddWithValue("$seq", block.Sequence);
            cmd.Parameters.AddWithValue("$kind", NormalizeKind(block.Kind));
            cmd.Parameters.AddWithValue("$tag", NormalizeTag(tag));
            cmd.Parameters.AddWithValue("$call", (object?)block.CallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$chars", block.CharCount);
            cmd.Parameters.AddWithValue("$preview", block.Preview ?? string.Empty);
            cmd.Parameters.AddWithValue("$content", block.Content ?? string.Empty);
            cmd.Parameters.AddWithValue("$ref", (object?)block.ContentRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$trunc", block.Truncated ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    private static void UpsertTurn(
        SqliteConnection conn,
        SqliteTransaction tx,
        TurnSummary turn,
        long turnSeq
    )
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO turns(turn_id, turn_seq, iter_end, user_preview, final_block_id, blocks, tools, failed, duration_ms, chars, status, last_request_id, updated_at)"
            + " VALUES($tid, $seq, $iterEnd, $userPreview, $finalBid, $blocks, $tools, $failed, $dur, $chars, $status, $req, $updated)"
            + " ON CONFLICT(turn_id) DO UPDATE SET"
            + " iter_end=excluded.iter_end,"
            + " blocks=turns.blocks+excluded.blocks,"
            + " tools=turns.tools+excluded.tools,"
            + " failed=turns.failed+excluded.failed,"
            + " chars=turns.chars+excluded.chars,"
            + " duration_ms=turns.duration_ms+excluded.duration_ms,"
            + " final_block_id=excluded.final_block_id,"
            + " status=excluded.status,"
            + " last_request_id=excluded.last_request_id,"
            + " updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$tid", turn.TurnId);
        cmd.Parameters.AddWithValue("$seq", turnSeq);
        cmd.Parameters.AddWithValue("$iterEnd", turn.IterEnd);
        cmd.Parameters.AddWithValue("$userPreview", turn.UserPreview ?? string.Empty);
        cmd.Parameters.AddWithValue("$finalBid", (object?)turn.FinalBlockId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blocks", turn.Blocks);
        cmd.Parameters.AddWithValue("$tools", turn.Tools);
        cmd.Parameters.AddWithValue("$failed", turn.Failed);
        cmd.Parameters.AddWithValue("$dur", turn.DurationMs);
        cmd.Parameters.AddWithValue("$chars", turn.Chars);
        cmd.Parameters.AddWithValue("$status", NormalizeStatus(turn.Status));
        cmd.Parameters.AddWithValue("$req", turn.LastRequestId ?? string.Empty);
        string updated = string.IsNullOrWhiteSpace(turn.UpdatedAtUtc)
            ? DateTime.UtcNow.ToString("o")
            : turn.UpdatedAtUtc;
        cmd.Parameters.AddWithValue("$updated", updated);
        cmd.ExecuteNonQuery();
    }

    private static string NormalizeKind(string? kind)
    {
        if (string.Equals(kind, TimelineBlockKind.Reasoning, StringComparison.Ordinal))
            return TimelineBlockKind.Reasoning;
        if (string.Equals(kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal))
            return TimelineBlockKind.ToolCall;
        if (string.Equals(kind, TimelineBlockKind.ToolResult, StringComparison.Ordinal))
            return TimelineBlockKind.ToolResult;
        return TimelineBlockKind.Text;
    }

    private static string NormalizeTag(string? tag)
    {
        if (string.Equals(tag, TimelineBlockTag.Final, StringComparison.Ordinal))
            return TimelineBlockTag.Final;
        return TimelineBlockTag.Process;
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.Equals(status, TimelineTurnStatus.Truncated, StringComparison.Ordinal))
            return TimelineTurnStatus.Truncated;
        if (string.Equals(status, TimelineTurnStatus.Cancelled, StringComparison.Ordinal))
            return TimelineTurnStatus.Cancelled;
        if (string.Equals(status, TimelineTurnStatus.Error, StringComparison.Ordinal))
            return TimelineTurnStatus.Error;
        return TimelineTurnStatus.Completed;
    }
}
