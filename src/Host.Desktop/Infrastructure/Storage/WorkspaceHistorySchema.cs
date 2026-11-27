using Host.Desktop.Infrastructure.Logging;
using Microsoft.Data.Sqlite;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区级历史库的目标结构基线：建库语句、结构版本常量、
/// 已有库版本校验与会话格式路由断言。
/// 块与轮次主键为全局唯一（生成策略保证跨会话不碰撞；同值跨会话写入按覆盖或合并处理，不提供隔离）。
/// 消息键为会话内复合唯一，允许跨会话同值共存。
/// 权威版本定义见目标架构规范；元数据文件为真相源，本类仅表达库侧结构。
/// </summary>
public static class WorkspaceHistorySchema
{
    /// <summary>当前工作区库结构版本：建库写入与版本校验的唯一口径。</summary>
    public const int CurrentFormatVersion = 3;

    /// <summary>
    /// 返回目标库的数据定义语句：会话注册表、三张业务表与三组复合索引。
    /// 仅含 DDL，不含版本标记语句；版本标记由 <see cref="EnsureSchema"/> 单独执行。
    /// </summary>
    public static string BuildCreateSql() =>
        "CREATE TABLE IF NOT EXISTS sessions("
        + "session_id TEXT PRIMARY KEY,"
        + "storage_format INTEGER NOT NULL,"
        + "session_kind TEXT NOT NULL CHECK(session_kind IN ('main','sub','archived')),"
        + "created_utc TEXT NOT NULL"
        + ");"
        + "CREATE TABLE IF NOT EXISTS messages("
        + "session_id TEXT NOT NULL,"
        + "seq INTEGER NOT NULL,"
        + "id TEXT NOT NULL,"
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
        + "created_utc TEXT NOT NULL,"
        + "PRIMARY KEY (session_id, seq),"
        + "UNIQUE (session_id, id)"
        + ");"
        + "CREATE TABLE IF NOT EXISTS blocks("
        + "session_id TEXT NOT NULL,"
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
        + "session_id TEXT NOT NULL,"
        + "turn_id TEXT PRIMARY KEY,"
        + "turn_seq INTEGER NOT NULL,"
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
        + "updated_at TEXT NOT NULL,"
        + "UNIQUE (session_id, turn_seq)"
        + ");"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_turn_seq ON blocks(session_id, turn_id, seq);"
        + "CREATE INDEX IF NOT EXISTS ix_blocks_call ON blocks(session_id, call_id) WHERE call_id IS NOT NULL;"
        + "CREATE INDEX IF NOT EXISTS ix_messages_kind ON messages(session_id, kind) WHERE kind IS NOT NULL;";

    /// <summary>
    /// 在给定连接上建表并对齐结构版本。
    /// 空库（尚无业务表）允许初始化并写入当前版本；
    /// 已有业务表但版本标记不是当前值的库一律拒绝，不做迁移与静默兼容。
    /// </summary>
    public static void EnsureSchema(SqliteConnection conn)
    {
        if (conn == null)
        {
            throw new ArgumentNullException(nameof(conn));
        }

        bool hasBusinessTables = HasAnyBusinessTable(conn);
        int current = ReadUserVersion(conn);
        if (hasBusinessTables && current != CurrentFormatVersion)
        {
            throw new UnsupportedFormatException(
                "Unsupported workspace library version "
                    + current
                    + ", expected "
                    + CurrentFormatVersion
                    + "."
            );
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = BuildCreateSql();
            cmd.ExecuteNonQuery();
        }

        using (var version = conn.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = " + CurrentFormatVersion + ";";
            version.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 会话格式路由断言：合法工作区会话通过；未标注老会话抛用户可读异常，
    /// 界面据此列表可见但拒绝打开；其余非法值按数据异常拒绝并记日志。
    /// 仅作用于打开会话后的正文读入口，不作用于会话列表。
    /// </summary>
    public static void EnsureSupportedFormat(int? format, string? sessionId = null)
    {
        if (format == CurrentFormatVersion)
        {
            return;
        }

        if (!format.HasValue)
        {
            throw new LegacySessionFormatException("旧格式不支持：该会话由更早版本创建，无法在当前版本打开。");
        }

        try
        {
            HaloLogger.App.Error(
                "[WorkspaceSchema] Unsupported session format sess="
                    + (sessionId ?? string.Empty)
                    + " format="
                    + format.Value
            );
        }
        catch { }

        throw new UnsupportedFormatException(
            "Unsupported session format "
                + format.Value
                + " for session '"
                + (sessionId ?? string.Empty)
                + "'."
        );
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        object? value = cmd.ExecuteScalar();
        if (value == null || value == DBNull.Value)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasAnyBusinessTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"
            + " AND name IN ('sessions','messages','blocks','turns');";
        object? value = cmd.ExecuteScalar();
        if (value == null || value == DBNull.Value)
        {
            return false;
        }

        try
        {
            return Convert.ToInt32(value) > 0;
        }
        catch
        {
            return false;
        }
    }
}
