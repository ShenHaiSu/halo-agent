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
        + "final_block_id TEXT,"
        + "blocks INT NOT NULL DEFAULT 0,"
        + "tools INT NOT NULL DEFAULT 0,"
        + "failed INT NOT NULL DEFAULT 0,"
        + "duration_ms INT NOT NULL DEFAULT 0,"
        + "chars INT NOT NULL DEFAULT 0,"
        + "status TEXT NOT NULL DEFAULT 'completed'"
        + " CHECK(status IN ('completed','truncated','cancelled','error')),"
        + "last_request_id TEXT NOT NULL DEFAULT '',"
        + "updated_at TEXT NOT NULL"
        + ");"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_turn_seq ON blocks(turn_id, seq);"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_call ON blocks(call_id) WHERE call_id IS NOT NULL;";

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
