using System.Text.Json;
using Host.Desktop.Core.Models;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>P007：压缩桩读写（存储层只认表与列，不认识业务）。</summary>
public sealed partial class SessionHistoryStore
{
    /// <summary>最后一个桩的 seq（无桩返回 null）。DB 快路径的唯一查询点。</summary>
    public async Task<long?> GetLastCompactStubSeqAsync(
        string ws,
        string sess,
        CancellationToken ct = default
    )
    {
        _db.EnsureDatabase(ws, sess);
        using var conn = _db.Open(ws, sess);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(seq) FROM messages WHERE kind='compact_stub';";
        object? v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v == null || v == DBNull.Value)
            return null;
        return Convert.ToInt64(v);
    }

    /// <summary>最后一个桩的完整视图（含 meta_json / usage）；无桩返回 null。</summary>
    public async Task<CompactStubInfo?> GetLastCompactStubAsync(
        string ws,
        string sess,
        CancellationToken ct = default
    )
    {
        _db.EnsureDatabase(ws, sess);
        using var conn = _db.Open(ws, sess);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, seq, content, input_tokens, output_tokens, cached_tokens, meta_json, created_utc"
            + " FROM messages WHERE kind='compact_stub' ORDER BY seq DESC LIMIT 1;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        string msgId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        if (string.IsNullOrWhiteSpace(msgId))
            return null;
        long? seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        string summary = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        long inTok = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        long outTok = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
        long cached = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
        string? metaJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        DateTime created = DateTime.UtcNow;
        if (!reader.IsDBNull(7))
        {
            try
            {
                string raw = reader.GetString(7);
                if (
                    !DateTime.TryParse(
                        raw,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var p
                    )
                )
                    DateTime.TryParse(raw, out p);
                if (p != default)
                    created = p;
            }
            catch { }
        }

        int removed = 0;
        int summaryTokens = (int)outTok;
        int promptTokens = (int)inTok;
        int cachedTokens = (int)cached;
        if (!string.IsNullOrWhiteSpace(metaJson))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<CompactStubMeta>(
                    metaJson,
                    HaloJsonOptions.CompactCamelCase
                );
                if (meta != null)
                {
                    removed = meta.RemovedCount;
                    if (meta.SummaryTokens > 0)
                        summaryTokens = meta.SummaryTokens;
                    if (meta.PromptTokens > 0)
                        promptTokens = meta.PromptTokens;
                    if (meta.CachedTokens > 0)
                        cachedTokens = meta.CachedTokens;
                }
            }
            catch { }
        }

        string turnId = "cturn-" + msgId;
        try
        {
            using var tcmd = conn.CreateCommand();
            tcmd.CommandText =
                "SELECT turn_id FROM turns WHERE kind='compact' ORDER BY turn_seq DESC LIMIT 1;";
            object? t = await tcmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (t != null && t != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(t)))
                turnId = Convert.ToString(t)!;
        }
        catch { }

        return new CompactStubInfo
        {
            MessageId = msgId,
            TurnId = turnId,
            Seq = seq,
            Summary = summary,
            SummaryTokens = summaryTokens,
            PromptTokens = promptTokens,
            CachedTokens = cachedTokens,
            RemovedCount = removed,
            CreatedAtUtc = created,
        };
    }

    /// <summary>按 seq 增量读（seq &gt;= sinceSeq，ORDER BY seq）。</summary>
    public async Task<List<UnifiedChatMessage>> GetMessagesSinceSeqAsync(
        string ws,
        string sess,
        long sinceSeq,
        CancellationToken ct = default
    )
    {
        var result = new List<UnifiedChatMessage>();
        _db.EnsureDatabase(ws, sess);
        using var conn = _db.Open(ws, sess);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, kind, meta_json, created_utc"
            + " FROM messages WHERE seq >= $since ORDER BY seq;";
        cmd.Parameters.AddWithValue("$since", sinceSeq);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var msg = MapCompactMessage(reader);
                if (msg != null)
                    result.Add(msg);
            }
            catch { }
        }
        return result;
    }

    /// <summary>桩 + 桩 turn 的单事务写入（messages INSERT OR IGNORE + turns UPSERT）。</summary>
    public async Task<long> InsertCompactStubAsync(
        string ws,
        string sess,
        UnifiedChatMessage stubMessage,
        TurnSummary stubTurn,
        CancellationToken ct = default
    )
    {
        if (stubMessage == null || string.IsNullOrWhiteSpace(stubMessage.Id))
            throw new ArgumentException("桩消息不能为空", nameof(stubMessage));
        if (stubTurn == null || string.IsNullOrWhiteSpace(stubTurn.TurnId))
            throw new ArgumentException("桩轮不能为空", nameof(stubTurn));
        var gate = GetWriteLock(ws, sess);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(ws, sess);
            using var conn = _db.Open(ws, sess);
            ApplyFullSynchronous(conn);
            using var tx = conn.BeginTransaction();
            InsertMessages(conn, tx, new[] { stubMessage });
            long turnSeq = ResolveTurnSeq(conn, tx, stubTurn);
            UpsertTurn(conn, tx, stubTurn, turnSeq);
            tx.Commit();
            return turnSeq;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>P007：DB 行→实体映射（与 GetMessagesAsync 同一列序，避免列漂移；仓储快路径复用）。</summary>
    internal static UnifiedChatMessage? MapCompactMessage(SqliteDataReader reader)
    {
        string id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        if (string.IsNullOrWhiteSpace(id))
            return null;
        DateTime createdUtc = DateTime.UtcNow;
        if (!reader.IsDBNull(13))
        {
            try
            {
                string rawUtc = reader.GetString(13);
                if (
                    !DateTime.TryParse(
                        rawUtc,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsedUtc
                    )
                )
                    DateTime.TryParse(rawUtc, out parsedUtc);
                if (parsedUtc != default)
                    createdUtc = parsedUtc;
            }
            catch { }
        }
        var msg = new UnifiedChatMessage { Id = id, CreatedAtUtc = createdUtc };
        msg.Role = reader.IsDBNull(1) ? "assistant" : reader.GetString(1);
        msg.Content = reader.IsDBNull(2) ? null : reader.GetString(2);
        msg.ReasoningContent = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (!reader.IsDBNull(4))
        {
            string toolsJson = reader.GetString(4);
            if (!string.IsNullOrWhiteSpace(toolsJson))
            {
                try
                {
                    msg.ToolCalls = JsonSerializer.Deserialize<
                        List<LlmGateway.Models.UnifiedToolCall>
                    >(toolsJson, HaloJsonOptions.CompactCamelCase);
                }
                catch { }
            }
        }
        if (!reader.IsDBNull(5))
            msg.ToolCallId = reader.GetString(5);
        if (!reader.IsDBNull(6))
            msg.Name = reader.GetString(6);
        if (!reader.IsDBNull(7))
        {
            try
            {
                msg.Images = JsonSerializer.Deserialize<List<ChatImageRef>>(
                    reader.GetString(7),
                    HaloJsonOptions.CompactCamelCase
                );
            }
            catch { }
        }
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
        return msg;
    }
}
