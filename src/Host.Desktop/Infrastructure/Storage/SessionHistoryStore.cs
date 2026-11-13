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
                "SELECT block_id, kind, tag, call_id, seq, char_count, preview, content, content_ref, truncated"
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
            long seq = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            string preview = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            string content = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            string? contentRef = reader.IsDBNull(8) ? null : reader.GetString(8);
            bool truncated = !reader.IsDBNull(9) && reader.GetInt32(9) != 0;
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
                Seq = seq,
                Kind = NormalizeKind(kind),
                Tag = NormalizeTag(tag),
                Preview = preview ?? string.Empty,
                Truncated = truncated || !string.IsNullOrWhiteSpace(contentRef),
            };
            if (string.Equals(item.Kind, TimelineBlockKind.ToolResult, StringComparison.Ordinal))
            {
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
            "SELECT kind, content, content_ref FROM blocks"
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
        string resolved = content;
        if (!string.IsNullOrWhiteSpace(contentRef))
        {
            string? fromFile = TryReadContentRef(workspaceId, sessionId, contentRef);
            if (fromFile != null)
            {
                resolved = fromFile;
            }
        }
        if (string.Equals(kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal))
        {
            return new TimelineToolResult
            {
                ArgsJson = resolved,
                ResultText = string.Empty,
                ResultRef = null
            };
        }
        if (!string.IsNullOrWhiteSpace(contentRef))
        {
            return new TimelineToolResult
            {
                ArgsJson = string.Empty,
                ResultText = string.Empty,
                ResultRef = contentRef
            };
        }
        return new TimelineToolResult
        {
            ArgsJson = string.Empty,
            ResultText = resolved,
            ResultRef = null
        };
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
