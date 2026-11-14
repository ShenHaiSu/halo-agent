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
    public const long LargeBlockThresholdBytes = 64L * 1024;
    public const int LargeBlockPreviewChars = 4096;

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
            ApplyLargeBlockPolicy(workspaceId, sessionId, blocks);
            using var conn = _db.Open(workspaceId, sessionId);
            ApplyFullSynchronous(conn);
            using var tx = conn.BeginTransaction();
            long turnSeq = ResolveTurnSeq(conn, tx, turn);
            FlipStaleFinal(conn, tx, turn.TurnId, ResolveNewIter(blocks, turn));
            InsertMessages(conn, tx, messages);
            InsertBlocks(conn, tx, blocks);
            UpsertTurn(conn, tx, turn, turnSeq);
            tx.Commit();
            watch.Stop();
            try
            {
                TimelineObservability.ReportFlush(watch.ElapsedMilliseconds);
            }
            catch { }
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

    /// <summary>发货加固：同轮新迭代落盘前，把旧迭代残留的 final 翻回 process（单事务，原子）。</summary>
    /// <returns>被翻转的 blockId（调用方可据此发布翻转事件；无翻转返回空表）。</returns>
    public async Task<List<string>> FlipFinalToProcessAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        int newIter,
        CancellationToken ct = default
    )
    {
        var flipped = new List<string>();
        if (string.IsNullOrWhiteSpace(turnId) || newIter <= 0)
        {
            return flipped;
        }
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            CollectStaleFinal(conn, tx, turnId, newIter, flipped);
            FlipStaleFinal(conn, tx, turnId, newIter);
            ClearFinalRefIfFlipped(conn, tx, turnId, flipped);
            tx.Commit();
            return flipped;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>发货加固：轮次以非完成态终结时落盘终态（截断/取消/错误），并把残留 final 降回 process。</summary>
    /// <returns>命中已落盘轮次返回 true；轮次尚无落盘行返回 false（调用方无需重试）。</returns>
    public async Task<bool> MarkTurnEndedAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        string status,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return false;
        }
        string ended = NormalizeStatus(status);
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            using var mark = conn.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "UPDATE turns SET status=$st, updated_at=$now WHERE turn_id=$tid;";
            mark.Parameters.AddWithValue("$st", ended);
            mark.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            mark.Parameters.AddWithValue("$tid", turnId);
            if (mark.ExecuteNonQuery() == 0)
            {
                tx.Rollback();
                return false;
            }
            if (!string.Equals(ended, TimelineTurnStatus.Completed, StringComparison.Ordinal))
            {
                using var demote = conn.CreateCommand();
                demote.Transaction = tx;
                demote.CommandText =
                    "UPDATE blocks SET tag='process' WHERE turn_id=$tid AND tag='final';";
                demote.Parameters.AddWithValue("$tid", turnId);
                demote.ExecuteNonQuery();
                using var clear = conn.CreateCommand();
                clear.Transaction = tx;
                clear.CommandText = "UPDATE turns SET final_block_id=NULL WHERE turn_id=$tid;";
                clear.Parameters.AddWithValue("$tid", turnId);
                clear.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>发货加固：会话打开时懒检（同进程同会话只检一次；永不抛错，不阻断打开）。</summary>
    public async Task<bool> CheckOnceAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            string key = workspaceId.Trim() + "/" + sessionId.Trim();
            if (!TimelineCheckCache.TryAdd(key, 0))
            {
                return true;
            }
            var (ok, _) = await CheckIntegrityAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearCheckCacheForTests() => TimelineCheckCache.Clear();

    private static readonly ConcurrentDictionary<string, byte> TimelineCheckCache = new();

    private static int ResolveNewIter(IReadOnlyList<TimelineBlock>? blocks, TurnSummary? turn)
    {
        int newest = turn?.IterEnd ?? 0;
        if (blocks != null)
        {
            foreach (var b in blocks)
            {
                if (b != null && b.Iteration > newest)
                {
                    newest = b.Iteration;
                }
            }
        }
        return newest;
    }

    private static void ApplyFullSynchronous(SqliteConnection conn)
    {
        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }
        catch { }
    }

    private static void CollectStaleFinal(
        SqliteConnection conn,
        SqliteTransaction tx,
        string turnId,
        int newIter,
        List<string> sink
    )
    {
        using var q = conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT block_id FROM blocks WHERE turn_id=$tid AND tag='final' AND iter < $iter;";
        q.Parameters.AddWithValue("$tid", turnId);
        q.Parameters.AddWithValue("$iter", newIter);
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(0))
            {
                string id = r.GetString(0);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    sink.Add(id);
                }
            }
        }
    }

    private static void FlipStaleFinal(
        SqliteConnection conn,
        SqliteTransaction tx,
        string? turnId,
        int newIter
    )
    {
        if (string.IsNullOrWhiteSpace(turnId) || newIter <= 0)
        {
            return;
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE blocks SET tag='process' WHERE turn_id=$tid AND tag='final' AND iter < $iter;";
        cmd.Parameters.AddWithValue("$tid", turnId);
        cmd.Parameters.AddWithValue("$iter", newIter);
        cmd.ExecuteNonQuery();
    }

    private static void ClearFinalRefIfFlipped(
        SqliteConnection conn,
        SqliteTransaction tx,
        string turnId,
        List<string> flipped
    )
    {
        if (flipped.Count == 0)
        {
            return;
        }
        foreach (string id in flipped)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE turns SET final_block_id=NULL, updated_at=$now WHERE turn_id=$tid AND final_block_id=$bid;";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$tid", turnId);
            cmd.Parameters.AddWithValue("$bid", id);
            cmd.ExecuteNonQuery();
        }
    }

    private void ReportWalSize(string workspaceId, string sessionId)
    {
        try
        {
            string dbPath = _db.GetDbPath(workspaceId, sessionId);
            string walPath = dbPath + "-wal";
            if (!File.Exists(walPath))
            {
                return;
            }
            long bytes = new FileInfo(walPath).Length;
            TimelineObservability.ReportWalSize(bytes);
        }
        catch { }
    }

    public async Task AppendToolResultAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        TimelineBlock resultBlock,
        bool isFailed,
        CancellationToken ct = default
    )
    {
        if (resultBlock == null || string.IsNullOrWhiteSpace(resultBlock.BlockId))
        {
            return;
        }
        if (!resultBlock.Success.HasValue)
        {
            resultBlock.Success = !isFailed;
        }
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            ApplyLargeBlockPolicy(workspaceId, sessionId, new[] { resultBlock });
            resultBlock.TurnId = string.IsNullOrWhiteSpace(resultBlock.TurnId)
                ? (turnId ?? string.Empty)
                : resultBlock.TurnId;
            if (
                string.Equals(
                    resultBlock.Tag,
                    TimelineBlockTag.Pending,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                resultBlock.Tag = TimelineBlockTag.Process;
            }
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            InsertBlocks(conn, tx, new[] { resultBlock });
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE turns SET tools = tools + 1, failed = failed + $failed,"
                + " chars = chars + $chars, updated_at = $updated WHERE turn_id = $tid;";
            cmd.Parameters.AddWithValue("$failed", isFailed ? 1 : 0);
            cmd.Parameters.AddWithValue("$chars", resultBlock.CharCount);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$tid", turnId ?? string.Empty);
            cmd.ExecuteNonQuery();
            tx.Commit();
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

    public async Task<(
        Dictionary<string, TimelineBlockContent> Items,
        List<string> Missing
    )> GetBlockContentsAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<string> blockIds,
        CancellationToken ct = default
    )
    {
        var items = new Dictionary<string, TimelineBlockContent>(StringComparer.Ordinal);
        var missing = new List<string>();
        if (blockIds == null || blockIds.Count == 0)
        {
            return (items, missing);
        }
        var distinct = blockIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            return (items, missing);
        }
        _db.EnsureDatabase(workspaceId, sessionId);
        using var conn = _db.Open(workspaceId, sessionId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string blockId in distinct)
        {
            if (!seen.Add(blockId))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT block_id, kind, tag, call_id, function_name, seq, char_count, preview, content, content_ref, truncated, success"
                + " FROM blocks WHERE block_id = $bid LIMIT 1;";
            cmd.Parameters.AddWithValue("$bid", blockId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                missing.Add(blockId);
                continue;
            }
            string kind = reader.IsDBNull(1) ? TimelineBlockKind.Text : reader.GetString(1);
            string tag = reader.IsDBNull(2) ? TimelineBlockTag.Process : reader.GetString(2);
            string? callId = reader.IsDBNull(3) ? null : reader.GetString(3);
            string? functionName = reader.IsDBNull(4) ? null : reader.GetString(4);
            long seq = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            string preview = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            string content = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            string? contentRef = reader.IsDBNull(9) ? null : reader.GetString(9);
            bool truncated = !reader.IsDBNull(10) && reader.GetInt32(10) != 0;
            bool? success = reader.IsDBNull(11) ? null : (reader.GetInt32(11) != 0);
            string resolved = content;
            string? resolvedRef = contentRef;
            if (!string.IsNullOrWhiteSpace(contentRef))
            {
                string? fromFile = TryReadContentRef(workspaceId, sessionId, contentRef);
                if (fromFile != null)
                {
                    resolved = fromFile;
                }
            }
            var item = new TimelineBlockContent
            {
                BlockId = blockId,
                CallId = callId,
                FunctionName = functionName,
                Seq = seq,
                Kind = NormalizeKind(kind),
                Tag = NormalizeTag(tag),
                Preview = preview ?? string.Empty,
                Truncated = truncated || !string.IsNullOrWhiteSpace(contentRef),
            };
            if (string.Equals(item.Kind, TimelineBlockKind.ToolResult, StringComparison.Ordinal))
            {
                item.Success = success;
                item.Markdown = string.Empty;
                if (!string.IsNullOrWhiteSpace(contentRef))
                {
                    item.ResultRef = contentRef;
                    item.ResultText = string.Empty;
                }
                else
                {
                    item.ResultText = resolved;
                }
            }
            else if (string.Equals(item.Kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal))
            {
                item.Markdown = resolved;
                item.ArgsJson = resolved;
            }
            else
            {
                item.Markdown = resolved;
            }
            if (
                !string.IsNullOrWhiteSpace(callId)
                && string.IsNullOrWhiteSpace(item.ArgsJson)
                && !string.Equals(item.Kind, TimelineBlockKind.ToolResult, StringComparison.Ordinal)
            )
            {
                item.ArgsJson = null;
            }
            items[blockId] = item;
        }
        return (items, missing);
    }

    public async Task<List<string>> GetBlockIdsByTurnAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        CancellationToken ct = default
    )
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return result;
        }
        _db.EnsureDatabase(workspaceId, sessionId);
        using var conn = _db.Open(workspaceId, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT block_id FROM blocks WHERE turn_id = $tid ORDER BY seq;";
        cmd.Parameters.AddWithValue("$tid", turnId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }
        return result;
    }

    public async Task<TimelineToolResult?> GetToolResultAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        string callId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(callId))
        {
            return null;
        }
        _db.EnsureDatabase(workspaceId, sessionId);
        using var conn = _db.Open(workspaceId, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT kind, content, content_ref, function_name, success FROM blocks"
            + " WHERE turn_id = $tid AND call_id = $cid ORDER BY seq DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$tid", turnId);
        cmd.Parameters.AddWithValue("$cid", callId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        string kind = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        string content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        string? contentRef = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? functionName = reader.IsDBNull(3) ? null : reader.GetString(3);
        bool? success = reader.IsDBNull(4) ? null : (reader.GetInt32(4) != 0);
        string resolved = content;
        if (!string.IsNullOrWhiteSpace(contentRef))
        {
            string? fromFile = TryReadContentRef(workspaceId, sessionId, contentRef);
            if (fromFile != null)
            {
                resolved = fromFile;
            }
        }
        if (string.IsNullOrWhiteSpace(functionName))
        {
            try
            {
                using var callCmd = conn.CreateCommand();
                callCmd.CommandText =
                    "SELECT function_name FROM blocks WHERE turn_id = $tid AND call_id = $cid"
                    + " AND kind = 'tool_call' ORDER BY seq LIMIT 1;";
                callCmd.Parameters.AddWithValue("$tid", turnId);
                callCmd.Parameters.AddWithValue("$cid", callId);
                using var callReader = await callCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await callReader.ReadAsync(ct).ConfigureAwait(false) && !callReader.IsDBNull(0))
                {
                    string? callFn = callReader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(callFn))
                    {
                        functionName = callFn;
                    }
                }
            }
            catch { }
        }
        if (string.Equals(kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal))
        {
            return new TimelineToolResult
            {
                ArgsJson = resolved,
                ResultText = string.Empty,
                ResultRef = null,
                FunctionName = functionName
            };
        }
        if (!string.IsNullOrWhiteSpace(contentRef))
        {
            return new TimelineToolResult
            {
                ArgsJson = string.Empty,
                ResultText = string.Empty,
                ResultRef = contentRef,
                FunctionName = functionName,
                Success = success
            };
        }
        return new TimelineToolResult
        {
            ArgsJson = string.Empty,
            ResultText = resolved,
            ResultRef = null,
            FunctionName = functionName,
            Success = success
        };
    }

    /// <summary>S3：DB messages 全量读（组包/预算/压缩统一入口，ORDER BY seq）。</summary>
    public async Task<List<UnifiedChatMessage>> GetMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        var result = new List<UnifiedChatMessage>();
        string dbPath = _db.Storage.GetHistoryDbPath(workspaceId, sessionId);
        if (!File.Exists(dbPath))
        {
            return result;
        }
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
        }
        catch
        {
            return result;
        }
        using var conn = _db.Open(workspaceId, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, created_utc"
            + " FROM messages ORDER BY seq;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                string id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                DateTime createdUtc = DateTime.UtcNow;
                if (!reader.IsDBNull(11))
                {
                    try
                    {
                        string rawUtc = reader.GetString(11);
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
                result.Add(msg);
            }
            catch { }
        }
        return result;
    }

    /// <summary>S3：单条消息镜像入 DB messages（Append 双写，INSERT OR IGNORE 幂等）。</summary>
    public void InsertSingleMessage(string workspaceId, string sessionId, UnifiedChatMessage msg)
    {
        if (msg == null || string.IsNullOrWhiteSpace(msg.Id))
            return;
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR IGNORE INTO messages(id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, created_utc)"
                + " VALUES($id, $role, $content, $reasoning, $tools, $tcid, $name, $imgs, $inTok, $outTok, $cached, $utc);";
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
                    toolsJson = JsonSerializer.Serialize(
                        msg.ToolCalls,
                        HaloJsonOptions.CompactCamelCase
                    );
            }
            catch { }
            cmd.Parameters.AddWithValue("$tools", (object?)toolsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tcid", (object?)msg.ToolCallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)msg.Name ?? DBNull.Value);
            string? imgsJson = null;
            try
            {
                if (msg.Images != null && msg.Images.Count > 0)
                    imgsJson = JsonSerializer.Serialize(
                        msg.Images,
                        HaloJsonOptions.CompactCamelCase
                    );
            }
            catch { }
            cmd.Parameters.AddWithValue("$imgs", (object?)imgsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inTok", (object?)msg.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outTok", (object?)msg.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cached", (object?)msg.CachedTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$utc", msg.CreatedAtUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>S3：按 turn_seq 截断（DELETE WHERE turn_seq &gt;= cut + 同步 messages/blocks）。</summary>
    public async Task TruncateFromTurnSeqAsync(
        string workspaceId,
        string sessionId,
        long cutTurnSeq,
        CancellationToken ct = default
    )
    {
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            var doomed = new List<string>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT turn_id FROM turns WHERE turn_seq >= $cut;";
                q.Parameters.AddWithValue("$cut", cutTurnSeq);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0))
                        doomed.Add(r.GetString(0));
                }
            }
            foreach (string tid in doomed)
            {
                using var d1 = conn.CreateCommand();
                d1.Transaction = tx;
                d1.CommandText = "DELETE FROM blocks WHERE turn_id = $tid;";
                d1.Parameters.AddWithValue("$tid", tid);
                d1.ExecuteNonQuery();
                using var d2 = conn.CreateCommand();
                d2.Transaction = tx;
                d2.CommandText = "DELETE FROM turns WHERE turn_id = $tid;";
                d2.Parameters.AddWithValue("$tid", tid);
                d2.ExecuteNonQuery();
            }
            // messages：turnId = uturn-&lt;userMsgId&gt;，删归属已删轮的用户/助手/工具行（按 turn 前缀启发 + 孤儿保留）
            // 为保零变不误删：仅删明确归属 doomed turn 的 user 行（id 为 turn 后缀）与其后连续 tool 行由上层 Rewrite 保证；
            // 此处保守：不删 messages（压缩/截断后 messages 由上层按 kept 重写）。预留事务一致性。
            tx.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>S3：按 kept 消息 Id 集合重写 messages 表（截断/删除/压缩后同步）。</summary>
    public async Task RewriteMessagesAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> kept,
        CancellationToken ct = default
    )
    {
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM messages;";
                del.ExecuteNonQuery();
            }
            InsertMessages(conn, tx, kept);
            tx.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>S3：删除单条 messages 行（DeleteMessage v2 路径）。</summary>
    public async Task DeleteMessageRowAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return;
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>S3：v1 分支出 v2 合成写入（assistant→text / tool→tool_result，09 §9.8）。</summary>
    public async Task SynthesizeV2FromMessagesAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> prefix,
        CancellationToken ct = default
    )
    {
        if (prefix == null || prefix.Count == 0)
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            return;
        }
        var gate = GetWriteLock(workspaceId, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            long maxTurnSeq = 0;
            long maxSeq = 0;
            using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT COALESCE(MAX(turn_seq),0) FROM turns;";
                object? v = q.ExecuteScalar();
                if (v != null && v != DBNull.Value)
                    maxTurnSeq = Convert.ToInt64(v);
            }
            using (var q2 = conn.CreateCommand())
            {
                q2.CommandText = "SELECT COALESCE(MAX(seq),0) FROM blocks;";
                object? v = q2.ExecuteScalar();
                if (v != null && v != DBNull.Value)
                    maxSeq = Convert.ToInt64(v);
            }
            // 按 assistant 切轮：每段 [user? + assistant + tool*] 为一 turn
            var turns = new List<TurnSummary>();
            var allBlocks = new List<TimelineBlock>();
            var allMsgs = new List<UnifiedChatMessage>(prefix);
            long turnSeq = maxTurnSeq;
            long seq = maxSeq;
            int i = 0;
            while (i < prefix.Count)
            {
                var m = prefix[i];
                string role = (m.Role ?? string.Empty).ToLowerInvariant();
                if (!string.Equals(role, "assistant", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }
                // 收集该 assistant 后的连续 tool
                var tools = new List<UnifiedChatMessage>();
                int j = i + 1;
                while (
                    j < prefix.Count
                    && string.Equals(
                        (prefix[j].Role ?? string.Empty).ToLowerInvariant(),
                        "tool",
                        StringComparison.Ordinal
                    )
                )
                {
                    tools.Add(prefix[j]);
                    j++;
                }
                turnSeq++;
                seq++;
                string turnId = "uturn-" + (m.Id ?? Guid.NewGuid().ToString("N"));
                // text 块：assistant content（最后一个非空为 final，其余 process；此处每轮单 assistant 即 final）
                string content = m.Content ?? string.Empty;
                bool hasText = !string.IsNullOrWhiteSpace(content);
                var textBlocks = new List<TimelineBlock>();
                if (hasText)
                {
                    seq++;
                    textBlocks.Add(
                        new TimelineBlock
                        {
                            BlockId = "synth:" + turnId + ":text",
                            TurnId = turnId,
                            Iteration = 1,
                            Sequence = seq,
                            Kind = TimelineBlockKind.Text,
                            Tag = TimelineBlockTag.Final,
                            CharCount = content.Length,
                            Preview = BuildPreviewText(content, 200),
                            Content = content,
                        }
                    );
                }
                foreach (var t in tools)
                {
                    seq++;
                    string callId = t.ToolCallId ?? string.Empty;
                    string tcontent = t.Content ?? string.Empty;
                    string preview = string.IsNullOrWhiteSpace(callId)
                        ? "[历史工具结果]"
                        : BuildPreviewText(tcontent, 200);
                    allBlocks.Add(
                        new TimelineBlock
                        {
                            BlockId =
                                "synth:"
                                + turnId
                                + ":"
                                + (
                                    string.IsNullOrWhiteSpace(callId)
                                        ? Guid.NewGuid().ToString("N")
                                        : callId
                                ),
                            TurnId = turnId,
                            Iteration = 1,
                            Sequence = seq,
                            Kind = TimelineBlockKind.ToolResult,
                            Tag = TimelineBlockTag.Process,
                            CallId = string.IsNullOrWhiteSpace(callId) ? null : callId,
                            CharCount = tcontent.Length,
                            Preview = preview,
                            Content = tcontent,
                        }
                    );
                }
                allBlocks.AddRange(textBlocks);
                string userPreview = string.Empty;
                // 前一个 user 作为本轮 userPreview
                for (int k = i - 1; k >= 0; k--)
                {
                    if (
                        string.Equals(
                            (prefix[k].Role ?? string.Empty).ToLowerInvariant(),
                            "user",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        userPreview = BuildPreviewText(prefix[k].Content ?? string.Empty, 15);
                        break;
                    }
                }
                turns.Add(
                    new TurnSummary
                    {
                        TurnId = turnId,
                        TurnSeq = turnSeq,
                        IterEnd = 1,
                        UserPreview = userPreview,
                        FinalBlockId = hasText ? "synth:" + turnId + ":text" : null,
                        FinalPreview = hasText ? BuildPreviewText(content, 60) : string.Empty,
                        Blocks = textBlocks.Count + tools.Count,
                        Tools = tools.Count,
                        Status = TimelineTurnStatus.Completed,
                        UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
                    }
                );
                i = j;
            }
            using var tx = conn.BeginTransaction();
            InsertMessages(conn, tx, allMsgs);
            InsertBlocks(conn, tx, allBlocks);
            foreach (var t in turns)
                UpsertTurn(conn, tx, t, t.TurnSeq);
            tx.Commit();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>S3：启动/GC 孤儿 tool-outputs 清理（fileRef 不在 blocks.content_ref 即删）。</summary>
    public async Task<int> CollectOrphanToolOutputsAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        int removed = 0;
        try
        {
            var refs = await GetDistinctContentRefsAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in refs)
            {
                string? rel = ParseContentRefRelative(r);
                if (!string.IsNullOrWhiteSpace(rel))
                    keep.Add(Path.GetFileName(rel!) ?? string.Empty);
            }
            string dir = _db.Storage.GetSessionToolOutputsDir(workspaceId, sessionId);
            if (!Directory.Exists(dir))
                return 0;
            foreach (string f in Directory.GetFiles(dir, "*.txt"))
            {
                ct.ThrowIfCancellationRequested();
                if (!keep.Contains(Path.GetFileName(f) ?? string.Empty))
                {
                    try
                    {
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>S3：DB 完整性校验（integrity_check + turns.blocks == count(blocks)）。</summary>
    public async Task<(bool ok, string detail)> CheckIntegrityAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        try
        {
            string existingDb = _db.GetDbPath(workspaceId, sessionId);
            if (!File.Exists(existingDb))
            {
                return (true, "no-db");
            }
            _db.EnsureDatabase(workspaceId, sessionId);
            using var conn = _db.Open(workspaceId, sessionId);
            using var c1 = conn.CreateCommand();
            c1.CommandText = "PRAGMA integrity_check;";
            object? v = await c1.ExecuteScalarAsync(ct).ConfigureAwait(false);
            string s = v?.ToString() ?? string.Empty;
            if (!string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    TimelineObservability.ReportIntegrity(false);
                }
                catch { }
                return (false, "integrity_check:" + s);
            }
            using var c2 = conn.CreateCommand();
            c2.CommandText =
                "SELECT turn_id, blocks, (SELECT COUNT(*) FROM blocks b WHERE b.turn_id = turns.turn_id) FROM turns;";
            using var r = await c2.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                int expect = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                // T2 追加的 tool_result 会使 blocks 计数滞后（AppendToolResult 只 +tools/+chars，不改 blocks）；
                // 此处允许实际 >= 物化值（只防丢失，不防多余），丢失即损坏。
                long actual = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                if (actual < expect)
                {
                    try
                    {
                        TimelineObservability.ReportIntegrity(false);
                    }
                    catch { }
                    return (false, "group-mismatch:" + (r.IsDBNull(0) ? "?" : r.GetString(0)));
                }
            }
            ReportWalSize(workspaceId, sessionId);
            try
            {
                TimelineObservability.ReportIntegrity(true);
            }
            catch { }
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<bool> CopyHistoryForBranchAsync(
        string srcWorkspaceId,
        string srcSessionId,
        string dstWorkspaceId,
        string dstSessionId,
        CancellationToken ct = default
    )
    {
        string srcDb = _db.Storage.GetHistoryDbPath(srcWorkspaceId, srcSessionId);
        if (!File.Exists(srcDb))
        {
            return false;
        }
        try
        {
            _db.Checkpoint(srcWorkspaceId, srcSessionId);
        }
        catch { }
        string dstDb = _db.Storage.GetHistoryDbPath(dstWorkspaceId, dstSessionId);
        string? dstDir = Path.GetDirectoryName(dstDb);
        if (!string.IsNullOrEmpty(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }
        try
        {
            File.Copy(srcDb, dstDb, overwrite: true);
        }
        catch
        {
            return false;
        }
        try
        {
            var refs = await GetDistinctContentRefsAsync(dstWorkspaceId, dstSessionId, ct)
                .ConfigureAwait(false);
            if (refs.Count == 0)
            {
                return true;
            }
            string srcOutputs = _db.Storage.GetSessionToolOutputsDir(srcWorkspaceId, srcSessionId);
            string dstOutputs = _db.Storage.GetSessionToolOutputsDir(dstWorkspaceId, dstSessionId);
            Directory.CreateDirectory(dstOutputs);
            foreach (string contentRef in refs)
            {
                string? rel = ParseContentRefRelative(contentRef);
                if (string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }
                string srcFile = Path.Combine(
                    _db.Storage.GetSessionDirectory(srcWorkspaceId, srcSessionId),
                    rel!.Replace('/', Path.DirectorySeparatorChar)
                );
                if (!File.Exists(srcFile))
                {
                    string absFallback = Path.Combine(srcOutputs, Path.GetFileName(rel!));
                    srcFile = absFallback;
                }
                if (!File.Exists(srcFile))
                {
                    continue;
                }
                string dstFile = Path.Combine(
                    _db.Storage.GetSessionDirectory(dstWorkspaceId, dstSessionId),
                    rel!.Replace('/', Path.DirectorySeparatorChar)
                );
                string? dir = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.Copy(srcFile, dstFile, overwrite: true);
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private async Task<List<string>> GetDistinctContentRefsAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        var result = new List<string>();
        _db.EnsureDatabase(workspaceId, sessionId);
        using var conn = _db.Open(workspaceId, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT content_ref FROM blocks WHERE content_ref IS NOT NULL;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                string v = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.Add(v);
                }
            }
        }
        return result;
    }

    private void ApplyLargeBlockPolicy(
        string workspaceId,
        string sessionId,
        IReadOnlyList<TimelineBlock>? blocks
    )
    {
        if (blocks == null || blocks.Count == 0)
        {
            return;
        }
        foreach (var block in blocks)
        {
            if (block == null)
            {
                continue;
            }
            string content = block.Content ?? string.Empty;
            long bytes;
            try
            {
                bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            }
            catch
            {
                bytes = content.Length;
            }
            if (bytes <= LargeBlockThresholdBytes || !string.IsNullOrWhiteSpace(block.ContentRef))
            {
                continue;
            }
            try
            {
                string dir = _db.Storage.GetSessionToolOutputsDir(workspaceId, sessionId);
                Directory.CreateDirectory(dir);
                string safeBlock = new string(
                    (block.BlockId ?? "block")
                        .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
                        .ToArray()
                );
                if (safeBlock.Length > 48)
                {
                    safeBlock = safeBlock.Substring(0, 48);
                }
                string fileName =
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + safeBlock + ".txt";
                string abs = Path.Combine(dir, fileName);
                File.WriteAllText(abs, content, System.Text.Encoding.UTF8);
                block.Content = string.Empty;
                block.ContentRef = "fileRef:tool-outputs/" + fileName;
                block.Preview = TimelineBlockTrackerBuildPreview(content, LargeBlockPreviewChars);
                try
                {
                    block.CharCount = content.Length;
                }
                catch { }
                block.Truncated = true;
            }
            catch
            {
                // Spill never breaks the write path; keep inline content on failure.
            }
        }
    }

    private string? TryReadContentRef(string workspaceId, string sessionId, string contentRef)
    {
        try
        {
            string? rel = ParseContentRefRelative(contentRef);
            if (!string.IsNullOrWhiteSpace(rel))
            {
                string abs = Path.Combine(
                    _db.Storage.GetSessionDirectory(workspaceId, sessionId),
                    rel!.Replace('/', Path.DirectorySeparatorChar)
                );
                if (File.Exists(abs))
                {
                    return File.ReadAllText(abs, System.Text.Encoding.UTF8);
                }
            }
            string raw = contentRef.Trim();
            const string prefix = "fileRef:";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = raw.Substring(prefix.Length).Trim().Trim('"');
                int spaceIdx = path.IndexOf(" size=", StringComparison.OrdinalIgnoreCase);
                if (spaceIdx > 0)
                {
                    path = path.Substring(0, spaceIdx).Trim().Trim('"');
                }
                path = path.TrimStart('"').TrimEnd('"');
                if (File.Exists(path))
                {
                    return File.ReadAllText(path, System.Text.Encoding.UTF8);
                }
                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    string guess = Path.Combine(
                        _db.Storage.GetSessionToolOutputsDir(workspaceId, sessionId),
                        fileName
                    );
                    if (File.Exists(guess))
                    {
                        return File.ReadAllText(guess, System.Text.Encoding.UTF8);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ParseContentRefRelative(string contentRef)
    {
        try
        {
            string raw = (contentRef ?? string.Empty).Trim();
            const string prefix = "fileRef:";
            if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string path = raw.Substring(prefix.Length).Trim().Trim('"');
            int spaceIdx = path.IndexOf(" size=", StringComparison.OrdinalIgnoreCase);
            if (spaceIdx > 0)
            {
                path = path.Substring(0, spaceIdx).Trim().Trim('"');
            }
            path = path.Replace('\\', '/').Trim();
            if (path.StartsWith("tool-outputs/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            if (!Path.IsPathRooted(path))
            {
                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return "tool-outputs/" + fileName;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPreviewText(string text, int maxGraphemes) =>
        TimelineBlockTrackerBuildPreview(text, maxGraphemes);

    private static string TimelineBlockTrackerBuildPreview(string text, int maxGraphemes)
    {
        if (string.IsNullOrEmpty(text) || maxGraphemes <= 0)
        {
            return string.Empty;
        }
        try
        {
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            if (elements.Length <= maxGraphemes)
            {
                return text;
            }
            int cut = elements[maxGraphemes];
            return text.Substring(0, cut);
        }
        catch
        {
            return text.Length <= maxGraphemes ? text : text.Substring(0, maxGraphemes);
        }
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
                "INSERT OR IGNORE INTO messages(id, role, content, reasoning, tool_calls_json, tool_call_id, name, images_json, input_tokens, output_tokens, cached_tokens, created_utc)"
                + " VALUES($id, $role, $content, $reasoning, $tools, $tcid, $name, $imgs, $inTok, $outTok, $cached, $utc);";
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
            cmd.Parameters.AddWithValue("$tcid", (object?)msg.ToolCallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)msg.Name ?? DBNull.Value);
            string? batchImgsJson = null;
            try
            {
                if (msg.Images != null && msg.Images.Count > 0)
                    batchImgsJson = JsonSerializer.Serialize(
                        msg.Images,
                        HaloJsonOptions.CompactCamelCase
                    );
            }
            catch { }
            cmd.Parameters.AddWithValue("$imgs", (object?)batchImgsJson ?? DBNull.Value);
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
                "INSERT OR REPLACE INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, function_name, success, char_count, preview, content, content_ref, truncated)"
                + " VALUES($bid, $tid, $iter, $seq, $kind, $tag, $call, $fn, $success, $chars, $preview, $content, $ref, $trunc);";
            cmd.Parameters.AddWithValue("$bid", block.BlockId);
            cmd.Parameters.AddWithValue("$tid", block.TurnId ?? string.Empty);
            cmd.Parameters.AddWithValue("$iter", block.Iteration);
            cmd.Parameters.AddWithValue("$seq", block.Sequence);
            string normalizedKind = NormalizeKind(block.Kind);
            cmd.Parameters.AddWithValue("$kind", normalizedKind);
            cmd.Parameters.AddWithValue("$tag", NormalizeTag(tag));
            cmd.Parameters.AddWithValue("$call", (object?)block.CallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fn", (object?)block.FunctionName ?? DBNull.Value);
            int? successInt =
                string.Equals(
                    normalizedKind,
                    TimelineBlockKind.ToolResult,
                    StringComparison.Ordinal
                ) && block.Success.HasValue
                    ? (block.Success.Value ? 1 : 0)
                    : null;
            cmd.Parameters.AddWithValue("$success", (object?)successInt ?? DBNull.Value);
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
