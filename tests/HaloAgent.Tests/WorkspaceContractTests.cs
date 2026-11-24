using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 工作区存储契约 tests：接口形态、目标结构基线、版本三态断言与元数据往返。
/// 仅覆盖新增定义，不触碰任何生产读写链路。
/// </summary>
public sealed class WorkspaceContractTests
{
    private static SqliteConnection OpenMemory()
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString()
        );
        conn.Open();
        return conn;
    }

    private static int ReadVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void WriteVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = " + version + ";";
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public void BuildCreateSql_ContainsWorkspaceTablesAndCompositeIndexes()
    {
        string sql = WorkspaceHistorySchema.BuildCreateSql();

        Assert.Contains("CREATE TABLE IF NOT EXISTS sessions(", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS messages(", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS blocks(", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS turns(", sql);
        Assert.Contains("PRIMARY KEY (session_id, seq)", sql);
        Assert.Contains("UNIQUE (session_id, id)", sql);
        Assert.Contains("UNIQUE (session_id, turn_seq)", sql);
        Assert.Contains("ix_blocks_turn_seq ON blocks(session_id, turn_id, seq)", sql);
        Assert.Contains("ix_blocks_call ON blocks(session_id, call_id)", sql);
        Assert.Contains("ix_messages_kind ON messages(session_id, kind)", sql);
    }

    [Fact]
    public void BuildCreateSql_DoesNotContainVersionStatement()
    {
        string sql = WorkspaceHistorySchema.BuildCreateSql();

        Assert.DoesNotContain("user_version", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRAGMA", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSchema_CreatesTablesAndWritesCurrentVersion()
    {
        using var conn = OpenMemory();

        WorkspaceHistorySchema.EnsureSchema(conn);

        Assert.True(TableExists(conn, "sessions"));
        Assert.True(TableExists(conn, "messages"));
        Assert.True(TableExists(conn, "blocks"));
        Assert.True(TableExists(conn, "turns"));
        Assert.Equal(WorkspaceHistorySchema.CurrentFormatVersion, ReadVersion(conn));
        Assert.Equal(3, WorkspaceHistorySchema.CurrentFormatVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void EnsureSchema_RejectsLibrariesWithUnexpectedVersion(int version)
    {
        using var conn = OpenMemory();
        WorkspaceHistorySchema.EnsureSchema(conn);
        WriteVersion(conn, version);

        Assert.Throws<UnsupportedFormatException>(() => WorkspaceHistorySchema.EnsureSchema(conn));
    }

    [Fact]
    public void EnsureSupportedFormat_AcceptsCurrentVersion()
    {
        WorkspaceHistorySchema.EnsureSupportedFormat(3, "sess_demo");
    }

    [Fact]
    public void EnsureSupportedFormat_MissingMarkingThrowsReadableError()
    {
        var ex = Assert.Throws<LegacySessionFormatException>(
            () => WorkspaceHistorySchema.EnsureSupportedFormat(null, "sess_demo")
        );
        Assert.Contains("旧格式不支持", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void EnsureSupportedFormat_IllegalValueThrowsDataError(int version)
    {
        Assert.Throws<UnsupportedFormatException>(
            () => WorkspaceHistorySchema.EnsureSupportedFormat(version, "sess_demo")
        );
    }

    [Fact]
    public void SessionMetadata_RoundTripsNewFields()
    {
        var meta = new SessionMetadata
        {
            Id = "sess_demo",
            WorkspaceId = "ws_demo",
            StorageFormatVersion = 3,
            SessionKind = "sub",
        };

        string json = JsonSerializer.Serialize(meta);
        var restored = JsonSerializer.Deserialize<SessionMetadata>(json);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.StorageFormatVersion);
        Assert.Equal("sub", restored.SessionKind);
    }

    [Fact]
    public void SessionMetadata_MissingFieldsReadAsUnmarked()
    {
        const string legacyJson =
            "{\"Id\":\"sess_legacy\",\"WorkspaceId\":\"ws_demo\",\"Title\":\"old\"}";

        var restored = JsonSerializer.Deserialize<SessionMetadata>(legacyJson);

        Assert.NotNull(restored);
        Assert.Null(restored!.StorageFormatVersion);
        Assert.Equal("main", restored.SessionKind);
    }

    [Fact]
    public void TurnSummary_FinalContentDefaultsEmptyAndAcceptsText()
    {
        var summary = new TurnSummary();

        Assert.Equal(string.Empty, summary.FinalContent);
        summary.FinalContent = "末轮正文";
        Assert.Equal("末轮正文", summary.FinalContent);
    }

    [Fact]
    public void StoreContract_ReadSideExposesNoFullMessageList()
    {
        var readMethods = typeof(IWorkspaceSessionStore)
            .GetMethods()
            .Where(m =>
                m.Name == nameof(IWorkspaceSessionStore.GetSummaryAsync)
                || m.Name == nameof(IWorkspaceSessionStore.GetBlockIdsAsync)
                || m.Name == nameof(IWorkspaceSessionStore.GetToolResultAsync)
                || m.Name == nameof(IWorkspaceSessionStore.GetUserContentAsync)
            )
            .ToList();

        Assert.Equal(4, readMethods.Count);
        Assert.DoesNotContain(
            typeof(IWorkspaceSessionStore).GetMethods(),
            m => m.ReturnType == typeof(Task<List<UnifiedChatMessage>>)
        );
    }

    [Fact]
    public void StoreContract_BlockListCarriesPreviewPayload()
    {
        Assert.Equal(
            typeof(Task<List<TimelineBlockContent>>),
            typeof(IWorkspaceSessionStore)
                .GetMethod(nameof(IWorkspaceSessionStore.GetBlockIdsAsync))!
                .ReturnType
        );
    }

    private sealed class StubStore : IWorkspaceSessionStore
    {
        public Task<List<TurnSummary>> GetSummaryAsync(
            string workspaceId,
            string sessionId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<TurnSummary>());

        public Task<List<HistoryMessageRef>> GetMessageRefsAsync(
            string workspaceId,
            string sessionId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<HistoryMessageRef>());

        public Task<List<TimelineBlockContent>> GetBlockIdsAsync(
            string workspaceId,
            string sessionId,
            string turnId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<TimelineBlockContent>());

        public Task<TimelineToolResult?> GetToolResultAsync(
            string workspaceId,
            string sessionId,
            string turnId,
            string callId,
            CancellationToken ct = default
        ) => Task.FromResult<TimelineToolResult?>(null);

        public Task<string> GetUserContentAsync(
            string workspaceId,
            string sessionId,
            string turnId,
            CancellationToken ct = default
        ) => Task.FromResult(string.Empty);

        public Task<long> AppendTurnAsync(
            string workspaceId,
            string sessionId,
            IReadOnlyList<UnifiedChatMessage> messages,
            IReadOnlyList<TimelineBlock> blocks,
            TurnSummary turn,
            CancellationToken ct = default
        ) => Task.FromResult(0L);

        public Task AppendToolResultAsync(
            string workspaceId,
            string sessionId,
            string turnId,
            TimelineBlock resultBlock,
            bool isFailed,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task TruncateFromTurnAsync(
            string workspaceId,
            string sessionId,
            string anchorTurnId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task RewriteSessionAsync(
            string workspaceId,
            string sessionId,
            IReadOnlyList<UnifiedChatMessage> tail,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task RegisterSessionAsync(
            string workspaceId,
            string sessionId,
            int storageFormat,
            string sessionKind,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteSessionAsync(
            string workspaceId,
            string sessionId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<WorkspaceBackupInfo> BackupWorkspaceAsync(
            string workspaceId,
            CancellationToken ct = default
        ) => Task.FromResult(new WorkspaceBackupInfo { WorkspaceId = workspaceId });
    }

    [Fact]
    public async Task StoreContract_CanBeImplementedByStub()
    {
        IWorkspaceSessionStore store = new StubStore();

        Assert.Empty(await store.GetSummaryAsync("ws", "sess"));
        Assert.Empty(await store.GetBlockIdsAsync("ws", "sess", "turn"));
        Assert.Null(await store.GetToolResultAsync("ws", "sess", "turn", "call"));
        Assert.Equal(string.Empty, await store.GetUserContentAsync("ws", "sess", "turn"));
    }
}
