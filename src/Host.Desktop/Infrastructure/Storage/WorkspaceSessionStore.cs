using Host.Desktop.Core.Models;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区级会话仓储统一读模型实现：摘要 / 过程 / 工具结果三层懒加载，
/// 标题链按回合取用户全文。读入口统一先做会话格式断言，老会话拒绝打开。
/// 写与生命周期直接委托工作区库存储过程，保持单写入源。
/// </summary>
public sealed class WorkspaceSessionStore : IWorkspaceSessionStore
{
    /// <summary>摘要末轮正文载荷上限：超限仅回预览与块引用，按需懒加载。</summary>
    public const int SummaryFinalContentThresholdChars = 32 * 1024;

    private readonly StoragePathManager _storage;
    private readonly ISessionRepository _repository;
    private readonly SessionHistoryStore _inner;

    public WorkspaceSessionStore(StoragePathManager storage, ISessionRepository? repository = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _repository = repository ?? new SessionRepository(_storage);
        _inner = new SessionHistoryStore(new SessionHistoryDb(_storage));
    }

    internal WorkspaceSessionStore(
        StoragePathManager storage,
        SessionHistoryStore inner,
        ISessionRepository? repository = null
    )
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _repository = repository ?? new SessionRepository(_storage);
    }

    /// <summary>
    /// 打开会话后正文读入口前置断言。列表元数据读取不经过此处。
    /// 空元数据视为会话缺失，按数据异常拒绝，避免误读底层。
    /// </summary>
    private async Task<SessionMetadata> RequireSupportedFormatAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        var meta = await _repository
            .LoadSessionMetaAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (meta == null)
        {
            throw new UnsupportedFormatException(
                "Unsupported session '" + (sessionId ?? string.Empty) + "': metadata missing."
            );
        }

        WorkspaceHistorySchema.EnsureSupportedFormat(meta.StorageFormatVersion, sessionId);
        return meta;
    }

    public async Task<List<TurnSummary>> GetSummaryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        await RequireSupportedFormatAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        var db = new SessionHistoryDb(_storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        var result = new List<TurnSummary>();
        using var conn = db.OpenWorkspace(workspaceId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.turn_id, t.turn_seq, t.iter_end, t.user_preview, t.user_content,"
            + " t.final_block_id, t.blocks, t.tools, t.failed, t.duration_ms, t.chars,"
            + " t.status, t.last_request_id, t.kind, t.removed_messages, t.updated_at,"
            + " b.preview, b.content, b.content_ref"
            + " FROM turns t LEFT JOIN blocks b"
            + " ON b.block_id = t.final_block_id AND b.session_id = t.session_id"
            + " WHERE t.session_id = $sid ORDER BY t.turn_seq;";
        cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
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
                UserContent = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                FinalBlockId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Blocks = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Tools = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                Failed = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                DurationMs = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                Chars = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                Status = reader.IsDBNull(11) ? TimelineTurnStatus.Completed : reader.GetString(11),
                LastRequestId = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                Kind = reader.IsDBNull(13) ? "turn" : reader.GetString(13),
                RemovedMessages = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                UpdatedAtUtc = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            };
            summary.CreatedAtUtc = summary.UpdatedAtUtc;
            summary.AnchorKey = turnId;
            string finalPreview = reader.IsDBNull(16) ? string.Empty : reader.GetString(16);
            string finalContent = reader.IsDBNull(17) ? string.Empty : reader.GetString(17);
            string? contentRef = reader.IsDBNull(18) ? null : reader.GetString(18);
            summary.FinalPreview = finalPreview ?? string.Empty;
            string resolved = finalContent ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(contentRef))
            {
                string? fromFile = TryReadToolOutput(workspaceId, sessionId!, contentRef!);
                if (fromFile != null)
                {
                    resolved = fromFile;
                }
            }

            if (resolved.Length > SummaryFinalContentThresholdChars)
            {
                summary.FinalContent = string.Empty;
            }
            else
            {
                summary.FinalContent = resolved;
            }

            result.Add(summary);
        }

        return result;
    }

    public Task<List<HistoryMessageRef>> GetMessageRefsAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.GetMessageRefsAsync(workspaceId, sessionId, ct);

    public async Task<List<TimelineBlockContent>> GetBlockIdsAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        CancellationToken ct = default
    )
    {
        await RequireSupportedFormatAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        var result = new List<TimelineBlockContent>();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return result;
        }

        var db = new SessionHistoryDb(_storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        using var conn = db.OpenWorkspace(workspaceId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT block_id, kind, tag, call_id, function_name, seq, preview"
            + " FROM blocks WHERE session_id = $sid AND turn_id = $tid ORDER BY seq;";
        cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
        cmd.Parameters.AddWithValue("$tid", turnId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            result.Add(
                new TimelineBlockContent
                {
                    BlockId = reader.GetString(0),
                    Kind = reader.IsDBNull(1) ? TimelineBlockKind.Text : reader.GetString(1),
                    Tag = reader.IsDBNull(2) ? TimelineBlockTag.Process : reader.GetString(2),
                    CallId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    FunctionName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Seq = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    Preview = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                }
            );
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
        await RequireSupportedFormatAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        return await _inner
            .GetToolResultAsync(workspaceId, sessionId, turnId, callId, ct)
            .ConfigureAwait(false);
    }

    public async Task<string> GetUserContentAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        CancellationToken ct = default
    )
    {
        await RequireSupportedFormatAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return string.Empty;
        }

        var db = new SessionHistoryDb(_storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        using var conn = db.OpenWorkspace(workspaceId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT user_content FROM turns WHERE session_id = $sid AND turn_id = $tid LIMIT 1;";
        cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
        cmd.Parameters.AddWithValue("$tid", turnId);
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value == null || value == DBNull.Value
            ? string.Empty
            : Convert.ToString(value) ?? string.Empty;
    }

    public Task<long> AppendTurnAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> messages,
        IReadOnlyList<TimelineBlock> blocks,
        TurnSummary turn,
        CancellationToken ct = default
    )
    {
        return _inner.AppendTurnAsync(workspaceId, sessionId, messages, blocks, turn, ct);
    }

    public Task AppendToolResultAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        TimelineBlock resultBlock,
        bool isFailed,
        CancellationToken ct = default
    )
    {
        return _inner.AppendToolResultAsync(
            workspaceId,
            sessionId,
            turnId,
            resultBlock,
            isFailed,
            ct
        );
    }

    public async Task TruncateFromTurnAsync(
        string workspaceId,
        string sessionId,
        string anchorTurnId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(anchorTurnId))
        {
            return;
        }

        var db = new SessionHistoryDb(_storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        long anchorSeq = -1;
        using (var conn = db.OpenWorkspace(workspaceId))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT turn_seq FROM turns WHERE session_id = $sid AND turn_id = $tid LIMIT 1;";
            cmd.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
            cmd.Parameters.AddWithValue("$tid", anchorTurnId);
            object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value != null && value != DBNull.Value)
            {
                anchorSeq = Convert.ToInt64(value);
            }
        }

        if (anchorSeq < 0)
        {
            return;
        }

        await _inner
            .TruncateFromTurnSeqAsync(workspaceId, sessionId!, anchorSeq, ct)
            .ConfigureAwait(false);
    }

    public Task RewriteSessionAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> tail,
        CancellationToken ct = default
    )
    {
        return _inner.RewriteMessagesAsync(workspaceId, sessionId, tail, ct);
    }

    public Task RegisterSessionAsync(
        string workspaceId,
        string sessionId,
        int storageFormat,
        string sessionKind,
        CancellationToken ct = default
    )
    {
        return _inner.RegisterSessionAsync(workspaceId, sessionId, storageFormat, sessionKind, ct);
    }

    public Task DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        return _inner.DeleteSessionPartitionAsync(workspaceId, sessionId, ct);
    }

    public Task<WorkspaceBackupInfo> BackupWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        var db = new SessionHistoryDb(_storage);
        db.EnsureWorkspaceDatabase(workspaceId);
        try
        {
            db.CheckpointWorkspace(workspaceId);
        }
        catch { }

        string dbPath = _storage.GetWorkspaceDbPath(workspaceId);
        string backupPath = _storage.GetWorkspaceBackupPath(workspaceId);
        string? dir = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            string? backupDir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(backupDir))
            {
                foreach (
                    string stale in Directory.GetFiles(
                        backupDir,
                        Path.GetFileName(backupPath) + ".*.tmp"
                    )
                )
                {
                    try
                    {
                        File.Delete(stale);
                    }
                    catch { }
                }
            }
        }
        catch { }

        string tmpPath = backupPath + "." + DateTime.UtcNow.Ticks + ".tmp";
        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var vacuum = conn.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $tmp;";
            vacuum.Parameters.AddWithValue("$tmp", tmpPath);
            vacuum.ExecuteNonQuery();
        }

        File.Move(tmpPath, backupPath, overwrite: true);
        try
        {
            foreach (
                string stale in Directory.GetFiles(
                    Path.GetDirectoryName(tmpPath)!,
                    Path.GetFileName(backupPath) + ".*.tmp"
                )
            )
            {
                if (
                    !string.Equals(stale, tmpPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(stale)
                )
                {
                    try
                    {
                        File.Delete(stale);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return Task.FromResult(
            new WorkspaceBackupInfo
            {
                WorkspaceId = workspaceId ?? string.Empty,
                BackupPath = backupPath,
                CreatedAtUtc = DateTime.UtcNow,
            }
        );
    }

    private string? TryReadToolOutput(string workspaceId, string sessionId, string contentRef)
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
            string? relative = null;
            if (path.StartsWith("tool-outputs/", StringComparison.OrdinalIgnoreCase))
            {
                relative = path;
            }
            else if (!Path.IsPathRooted(path))
            {
                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    relative = "tool-outputs/" + fileName;
                }
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            string abs = Path.Combine(
                _storage.GetSessionDirectory(workspaceId, sessionId),
                relative!.Replace('/', Path.DirectorySeparatorChar)
            );
            if (File.Exists(abs))
            {
                return File.ReadAllText(abs, System.Text.Encoding.UTF8);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
