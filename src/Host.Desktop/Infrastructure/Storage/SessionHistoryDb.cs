using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// Per-session timeline database bootstrap.
/// One SQLite file per session directory: messages / blocks / turns plus covering indexes.
/// Connections are always short-lived: open, use, close. Never held across sessions.
/// </summary>
public sealed class SessionHistoryDb
{
    private readonly StoragePathManager _storage;

    public SessionHistoryDb(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public StoragePathManager Storage => _storage;

    public string GetDbPath(string workspaceId, string sessionId) =>
        _storage.GetHistoryDbPath(workspaceId, sessionId);

    public static string BuildCreateSql() =>
        "CREATE TABLE IF NOT EXISTS messages("
        + "seq INTEGER PRIMARY KEY,"
        + "id TEXT NOT NULL UNIQUE,"
        + "role TEXT NOT NULL,"
        + "content TEXT,"
        + "reasoning TEXT,"
        + "tool_calls_json TEXT,"
        + "tool_call_id TEXT,"
        + "name TEXT,"
        + "images_json TEXT,"
        + "input_tokens INT,"
        + "output_tokens INT,"
        + "cached_tokens INT,"
        + "kind TEXT,"
        + "meta_json TEXT,"
        + "created_utc TEXT NOT NULL"
        + ");"
        + "CREATE TABLE IF NOT EXISTS blocks("
        + "block_id TEXT PRIMARY KEY,"
        + "turn_id TEXT NOT NULL,"
        + "iter INT NOT NULL,"
        + "seq INT NOT NULL,"
        + "kind TEXT NOT NULL CHECK(kind IN ('reasoning','text','tool_call','tool_result')),"
        + "tag TEXT NOT NULL CHECK(tag IN ('process','final')),"
        + "call_id TEXT,"
        + "function_name TEXT,"
        + "success INTEGER,"
        + "char_count INT NOT NULL DEFAULT 0,"
        + "preview TEXT NOT NULL DEFAULT '',"
        + "content TEXT NOT NULL DEFAULT '',"
        + "content_ref TEXT,"
        + "truncated INT NOT NULL DEFAULT 0"
        + ");"
        + "CREATE TABLE IF NOT EXISTS turns("
        + "turn_id TEXT PRIMARY KEY,"
        + "turn_seq INTEGER NOT NULL UNIQUE,"
        + "iter_end INT NOT NULL,"
        + "user_preview TEXT NOT NULL DEFAULT '',"
        + "user_content TEXT NOT NULL DEFAULT '',"
        + "final_block_id TEXT,"
        + "blocks INT NOT NULL DEFAULT 0,"
        + "tools INT NOT NULL DEFAULT 0,"
        + "failed INT NOT NULL DEFAULT 0,"
        + "duration_ms INT NOT NULL DEFAULT 0,"
        + "chars INT NOT NULL DEFAULT 0,"
        + "status TEXT NOT NULL DEFAULT 'completed'"
        + " CHECK(status IN ('completed','truncated','cancelled','error')),"
        + "last_request_id TEXT NOT NULL DEFAULT '',"
        + "kind TEXT NOT NULL DEFAULT 'turn',"
        + "removed_messages INT NOT NULL DEFAULT 0,"
        + "updated_at TEXT NOT NULL"
        + ");"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_turn_seq ON blocks(turn_id, seq);"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_call ON blocks(call_id) WHERE call_id IS NOT NULL;"
        + "CREATE INDEX IF NOT EXISTS ix_messages_kind ON messages(kind) WHERE kind IS NOT NULL;";

    public string EnsureDatabase(string workspaceId, string sessionId)
    {
        string dbPath = GetDbPath(workspaceId, sessionId);
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var conn = OpenConnection(dbPath);
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = BuildCreateSql();
            cmd.ExecuteNonQuery();
        }
        try
        {
            using var cols = conn.CreateCommand();
            cols.CommandText = "PRAGMA table_info(messages);";
            using var r = cols.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                if (!r.IsDBNull(1))
                    names.Add(r.GetString(1));
            }
            r.Close();
            if (!names.Contains("tool_call_id"))
            {
                using var add = conn.CreateCommand();
                add.CommandText = "ALTER TABLE messages ADD COLUMN tool_call_id TEXT;";
                add.ExecuteNonQuery();
            }
            if (!names.Contains("name"))
            {
                using var add2 = conn.CreateCommand();
                add2.CommandText = "ALTER TABLE messages ADD COLUMN name TEXT;";
                add2.ExecuteNonQuery();
            }
            if (!names.Contains("images_json"))
            {
                using var add3 = conn.CreateCommand();
                add3.CommandText = "ALTER TABLE messages ADD COLUMN images_json TEXT;";
                add3.ExecuteNonQuery();
            }
            // P007: 压缩桩标记列（幂等，老库自动补列）。
            if (!names.Contains("kind"))
            {
                using var addK = conn.CreateCommand();
                addK.CommandText = "ALTER TABLE messages ADD COLUMN kind TEXT;";
                addK.ExecuteNonQuery();
            }
            if (!names.Contains("meta_json"))
            {
                using var addM = conn.CreateCommand();
                addM.CommandText = "ALTER TABLE messages ADD COLUMN meta_json TEXT;";
                addM.ExecuteNonQuery();
            }
            // P007: 桩查询快路径索引（体积极小，只索引桩行）。
            using var idx = conn.CreateCommand();
            idx.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_messages_kind ON messages(kind) WHERE kind IS NOT NULL;";
            idx.ExecuteNonQuery();
        }
        catch { }
        try
        {
            using var blockCols = conn.CreateCommand();
            blockCols.CommandText = "PRAGMA table_info(blocks);";
            using var br = blockCols.ExecuteReader();
            var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (br.Read())
            {
                if (!br.IsDBNull(1))
                    blockNames.Add(br.GetString(1));
            }
            br.Close();
            if (!blockNames.Contains("function_name"))
            {
                using var addFn = conn.CreateCommand();
                addFn.CommandText = "ALTER TABLE blocks ADD COLUMN function_name TEXT;";
                addFn.ExecuteNonQuery();
            }
            if (!blockNames.Contains("success"))
            {
                using var addSuccess = conn.CreateCommand();
                addSuccess.CommandText = "ALTER TABLE blocks ADD COLUMN success INTEGER;";
                addSuccess.ExecuteNonQuery();
            }
        }
        catch { }
        try
        {
            using var turnCols = conn.CreateCommand();
            turnCols.CommandText = "PRAGMA table_info(turns);";
            using var tr = turnCols.ExecuteReader();
            var turnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (tr.Read())
            {
                if (!tr.IsDBNull(1))
                    turnNames.Add(tr.GetString(1));
            }
            tr.Close();
            if (!turnNames.Contains("user_content"))
            {
                using var addContent = conn.CreateCommand();
                addContent.CommandText =
                    "ALTER TABLE turns ADD COLUMN user_content TEXT NOT NULL DEFAULT '';";
                addContent.ExecuteNonQuery();
            }
            // P007: 压缩轮标记列（默认 'turn'，老行自动回填）。
            if (!turnNames.Contains("kind"))
            {
                using var addTK = conn.CreateCommand();
                addTK.CommandText =
                    "ALTER TABLE turns ADD COLUMN kind TEXT NOT NULL DEFAULT 'turn';";
                addTK.ExecuteNonQuery();
            }
            // P007: 桩轮覆盖条数（普通轮恒 0；老行默认 0）。
            if (!turnNames.Contains("removed_messages"))
            {
                using var addRM = conn.CreateCommand();
                addRM.CommandText =
                    "ALTER TABLE turns ADD COLUMN removed_messages INT NOT NULL DEFAULT 0;";
                addRM.ExecuteNonQuery();
            }
            using var backfill = conn.CreateCommand();
            backfill.CommandText =
                "UPDATE turns SET user_content = COALESCE((SELECT content FROM messages WHERE id = substr(turn_id, 7) AND role = 'user'), '')"
                + " WHERE (user_content IS NULL OR user_content = '') AND turn_id LIKE 'uturn-%';";
            backfill.ExecuteNonQuery();
        }
        catch { }
        try
        {
            // P007: 老式字符串桩回填结构化桩（幂等：kind IS NULL 保证仅首跑有效）。
            // P007: 仅回填 messages；不写 meta_json（老桩无 usage，前端走兼容层）；不迁 turns（老轮非摘要前缀且 final_block_id 含 compact 前缀，LIKE 会误伤）。
            using var stubBackfill = conn.CreateCommand();
            stubBackfill.CommandText =
                "UPDATE messages SET kind = 'compact_stub'"
                + " WHERE kind IS NULL AND role = 'user' AND content LIKE '【上下文摘要%';";
            stubBackfill.ExecuteNonQuery();
        }
        catch { }
        return dbPath;
    }

    public SqliteConnection Open(string workspaceId, string sessionId) =>
        OpenConnection(GetDbPath(workspaceId, sessionId));

    public bool Checkpoint(string workspaceId, string sessionId)
    {
        string dbPath = GetDbPath(workspaceId, sessionId);
        if (!File.Exists(dbPath))
        {
            return true;
        }
        try
        {
            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
            TimelineObservability.ReportCheckpoint(true);
            return true;
        }
        catch
        {
            // Checkpoint failures are observed via counters; never break the write path here.
            try
            {
                TimelineObservability.ReportCheckpoint(false);
            }
            catch { }
            return false;
        }
    }

    public static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString()
        );
        conn.Open();
        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText =
                "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        catch
        {
            // Pragmas are best effort on open; DDL path sets the authoritative values.
        }

        return conn;
    }
}
