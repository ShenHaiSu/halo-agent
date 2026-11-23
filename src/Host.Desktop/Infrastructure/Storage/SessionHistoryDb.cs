using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区级时间轴库引导。
/// 每个工作区一个 SQLite 文件：sessions / messages / blocks / turns。
/// 连接均为短生命周期：用完即关，不跨会话持有。
/// </summary>
public sealed class SessionHistoryDb
{
    private readonly StoragePathManager _storage;

    public SessionHistoryDb(StoragePathManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public StoragePathManager Storage => _storage;

    public string GetWorkspaceDbPath(string workspaceId) =>
        _storage.GetWorkspaceDbPath(workspaceId);

    /// <summary>工作区单库建库入口：建 sessions 注册表与三张业务表并对齐结构版本。</summary>
    public string EnsureWorkspaceDatabase(string workspaceId)
    {
        string dbPath = GetWorkspaceDbPath(workspaceId);
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
        WorkspaceHistorySchema.EnsureSchema(conn);
        try
        {
            using var stubBackfill = conn.CreateCommand();
            stubBackfill.CommandText =
                "UPDATE messages SET kind = 'compact_stub'"
                + " WHERE kind IS NULL AND role = 'user' AND content LIKE '【上下文摘要%';";
            stubBackfill.ExecuteNonQuery();
        }
        catch { }
        return dbPath;
    }

    public SqliteConnection OpenWorkspace(string workspaceId) =>
        OpenConnection(GetWorkspaceDbPath(workspaceId));

    public bool CheckpointWorkspace(string workspaceId)
    {
        string dbPath = GetWorkspaceDbPath(workspaceId);
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
