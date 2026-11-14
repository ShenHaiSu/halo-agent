#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// History tool card reuse: function name persists from draft to store to point query.
/// </summary>
public sealed class TimelineS5Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionHistoryStore _store;
    private readonly SessionHistoryDb _db;

    public TimelineS5Tests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS5_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloTimelineS5Boot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _store = new SessionHistoryStore(_storageManager);
        _db = new SessionHistoryDb(_storageManager);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        try
        {
            Directory.Delete(_bootstrapRoot, true);
        }
        catch { }
    }

    private static TurnSummary MakeTurn(string turnId) =>
        new()
        {
            TurnId = turnId,
            TurnSeq = 0,
            IterEnd = 1,
            UserPreview = "u",
            Blocks = 2,
            Tools = 1,
            Status = TimelineTurnStatus.Completed,
            LastRequestId = "req-s5",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o")
        };

    [Fact]
    public void FinalizeKeepsFunctionName()
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "ws-s5",
            SessionId = "sess-s5final",
            UserText = "q",
            RequestId = "req-final"
        };
        ctx.UserMessage = UnifiedChatMessage.User("q");
        var toolDraft = new TimelineBlockDraft
        {
            BlockId = "req-final:b1",
            Kind = TimelineBlockKind.ToolCall,
            CallId = "call-keep",
            FunctionName = "exec_command",
            Iteration = 1,
            Sequence = 1
        };
        toolDraft.Args.Append("{\"command\":\"echo hi\"}");
        ctx.TimelineLog.Add(toolDraft);

        var (blocks, _) = TimelineFinalizer.Finalize(ctx, 1, TimelineTurnStatus.Completed);
        var hit = Assert.Single(blocks, b => b.CallId == "call-keep");
        Assert.Equal("exec_command", hit.FunctionName);
        var parsed = JsonDocument.Parse(hit.Content);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

    [Fact]
    public async Task BlockContentsReturnsFunctionName()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5contents";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "s5:b-call",
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.ToolCall,
                    Tag = TimelineBlockTag.Process,
                    CallId = "call-1",
                    FunctionName = "read_file",
                    CharCount = 10,
                    Preview = "{}",
                    Content = "{\"path\":\"a.txt\"}"
                },
                new()
                {
                    BlockId = "s5:b-result",
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = 2,
                    Kind = TimelineBlockKind.ToolResult,
                    Tag = TimelineBlockTag.Process,
                    CallId = "call-1",
                    FunctionName = "read_file",
                    CharCount = 5,
                    Preview = "hello",
                    Content = "hello"
                },
            },
            MakeTurn(turn)
        );

        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "s5:b-call", "s5:b-result" }
        );
        Assert.Empty(missing);
        Assert.Equal("read_file", items["s5:b-call"].FunctionName);
        Assert.Equal("read_file", items["s5:b-result"].FunctionName);
    }

    [Fact]
    public async Task OldRowsWithoutFunctionNameReturnNull()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5old";
        const string turn = "uturn-u1";
        string dbPath = _db.EnsureDatabase(ws, sess);
        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, function_name, char_count, preview, content, content_ref, truncated)"
                + " VALUES('s5:old-call', $tid, 1, 1, 'tool_call', 'process', 'call-old', NULL, 2, '{}', '{}', NULL, 0);";
            cmd.Parameters.AddWithValue("$tid", turn);
            cmd.ExecuteNonQuery();
        }

        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "s5:old-call" }
        );
        Assert.Empty(missing);
        Assert.Null(items["s5:old-call"].FunctionName);
    }

    [Fact]
    public void MigrationAddsFunctionNameColumn()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5migrate";
        string dbPath = _db.GetDbPath(ws, sess);
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var ddl = conn.CreateCommand();
            ddl.CommandText =
                "CREATE TABLE IF NOT EXISTS blocks("
                + "block_id TEXT PRIMARY KEY,"
                + "turn_id TEXT NOT NULL,"
                + "iter INT NOT NULL,"
                + "seq INT NOT NULL,"
                + "kind TEXT NOT NULL,"
                + "tag TEXT NOT NULL,"
                + "call_id TEXT,"
                + "char_count INT NOT NULL DEFAULT 0,"
                + "preview TEXT NOT NULL DEFAULT '',"
                + "content TEXT NOT NULL DEFAULT '',"
                + "content_ref TEXT,"
                + "truncated INT NOT NULL DEFAULT 0"
                + ");";
            ddl.ExecuteNonQuery();
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, char_count, preview, content)"
                + " VALUES('old:b1', 'uturn-old', 1, 1, 'text', 'process', NULL, 5, 'hello', 'hello');";
            ins.ExecuteNonQuery();
        }

        _db.EnsureDatabase(ws, sess);

        using (var check = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var pragma = check.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(blocks);";
            using var reader = pragma.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    names.Add(reader.GetString(1));
                }
            }
            Assert.Contains("function_name", names);
            using var query = check.CreateCommand();
            query.CommandText = "SELECT content FROM blocks WHERE block_id='old:b1';";
            object? content = query.ExecuteScalar();
            Assert.Equal("hello", Convert.ToString(content));
        }
    }

    [Fact]
    public async Task ToolResultCarriesFunctionName()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5toolresult";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "s5:tr-call",
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.ToolCall,
                    Tag = TimelineBlockTag.Process,
                    CallId = "call-9",
                    FunctionName = "exec_command",
                    CharCount = 4,
                    Preview = "{}",
                    Content = "{}"
                },
                new()
                {
                    BlockId = "s5:tr-result",
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = 2,
                    Kind = TimelineBlockKind.ToolResult,
                    Tag = TimelineBlockTag.Process,
                    CallId = "call-9",
                    FunctionName = "exec_command",
                    CharCount = 2,
                    Preview = "ok",
                    Content = "ok"
                },
            },
            MakeTurn(turn)
        );

        var toolResult = await _store.GetToolResultAsync(ws, sess, turn, "call-9");
        Assert.NotNull(toolResult);
        Assert.Equal("exec_command", toolResult!.FunctionName);
        Assert.Equal("ok", toolResult.ResultText);
    }

    [Fact]
    public async Task ToolResultSuccessRoundTrip()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5success";
        const string turn = "uturn-u1";
        await _store.AppendToolResultAsync(
            ws,
            sess,
            turn,
            new TimelineBlock
            {
                BlockId = "s5:ok-result",
                TurnId = turn,
                Iteration = 1,
                Sequence = 1,
                Kind = TimelineBlockKind.ToolResult,
                Tag = TimelineBlockTag.Process,
                CallId = "call-ok",
                FunctionName = "read_file",
                Success = true,
                CharCount = 2,
                Preview = "ok",
                Content = "ok"
            },
            isFailed: false
        );
        await _store.AppendToolResultAsync(
            ws,
            sess,
            turn,
            new TimelineBlock
            {
                BlockId = "s5:fail-result",
                TurnId = turn,
                Iteration = 1,
                Sequence = 2,
                Kind = TimelineBlockKind.ToolResult,
                Tag = TimelineBlockTag.Process,
                CallId = "call-fail",
                FunctionName = "exec_command",
                Success = false,
                CharCount = 3,
                Preview = "bad",
                Content = "bad"
            },
            isFailed: true
        );

        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "s5:ok-result", "s5:fail-result" }
        );
        Assert.Empty(missing);
        Assert.Equal(true, items["s5:ok-result"].Success);
        Assert.Equal(false, items["s5:fail-result"].Success);

        var okResult = await _store.GetToolResultAsync(ws, sess, turn, "call-ok");
        Assert.NotNull(okResult);
        Assert.Equal(true, okResult!.Success);
        var failResult = await _store.GetToolResultAsync(ws, sess, turn, "call-fail");
        Assert.NotNull(failResult);
        Assert.Equal(false, failResult!.Success);
    }

    [Fact]
    public async Task ToolCallRowsKeepNullSuccess()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5callsuccess";
        const string turn = "uturn-u1";
        await _store.AppendTurnAsync(
            ws,
            sess,
            new List<UnifiedChatMessage>(),
            new List<TimelineBlock>
            {
                new()
                {
                    BlockId = "s5:call-only",
                    TurnId = turn,
                    Iteration = 1,
                    Sequence = 1,
                    Kind = TimelineBlockKind.ToolCall,
                    Tag = TimelineBlockTag.Process,
                    CallId = "call-only",
                    FunctionName = "read_file",
                    Success = true,
                    CharCount = 2,
                    Preview = "{}",
                    Content = "{}"
                },
            },
            MakeTurn(turn)
        );

        var (items, _) = await _store.GetBlockContentsAsync(ws, sess, new[] { "s5:call-only" });
        Assert.Null(items["s5:call-only"].Success);
    }

    [Fact]
    public async Task LegacyRowsReadBackAsUnknown()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5legacy";
        const string turn = "uturn-u1";
        string dbPath = _db.EnsureDatabase(ws, sess);
        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, function_name, char_count, preview, content, content_ref, truncated)"
                + " VALUES('s5:legacy-result', $tid, 1, 1, 'tool_result', 'process', 'call-legacy', 'read_file', 5, 'hello', 'hello', NULL, 0);";
            cmd.Parameters.AddWithValue("$tid", turn);
            cmd.ExecuteNonQuery();
        }

        var (items, missing) = await _store.GetBlockContentsAsync(
            ws,
            sess,
            new[] { "s5:legacy-result" }
        );
        Assert.Empty(missing);
        Assert.Null(items["s5:legacy-result"].Success);
        var toolResult = await _store.GetToolResultAsync(ws, sess, turn, "call-legacy");
        Assert.NotNull(toolResult);
        Assert.Null(toolResult!.Success);
    }

    [Fact]
    public void MigrationAddsSuccessColumn()
    {
        const string ws = "ws-s5";
        const string sess = "sess-s5successmigrate";
        string dbPath = _db.GetDbPath(ws, sess);
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
        using (var conn = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var ddl = conn.CreateCommand();
            ddl.CommandText =
                "CREATE TABLE IF NOT EXISTS blocks("
                + "block_id TEXT PRIMARY KEY,"
                + "turn_id TEXT NOT NULL,"
                + "iter INT NOT NULL,"
                + "seq INT NOT NULL,"
                + "kind TEXT NOT NULL,"
                + "tag TEXT NOT NULL,"
                + "call_id TEXT,"
                + "function_name TEXT,"
                + "char_count INT NOT NULL DEFAULT 0,"
                + "preview TEXT NOT NULL DEFAULT '',"
                + "content TEXT NOT NULL DEFAULT '',"
                + "content_ref TEXT,"
                + "truncated INT NOT NULL DEFAULT 0"
                + ");";
            ddl.ExecuteNonQuery();
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO blocks(block_id, turn_id, iter, seq, kind, tag, call_id, char_count, preview, content)"
                + " VALUES('old:b1', 'uturn-old', 1, 1, 'tool_result', 'process', 'call-old', 5, 'hello', 'hello');";
            ins.ExecuteNonQuery();
        }

        _db.EnsureDatabase(ws, sess);
        _db.EnsureDatabase(ws, sess);

        using (var check = SessionHistoryDb.OpenConnection(dbPath))
        {
            using var pragma = check.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(blocks);";
            using var reader = pragma.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    names.Add(reader.GetString(1));
                }
            }
            Assert.Contains("success", names);
            using var query = check.CreateCommand();
            query.CommandText = "SELECT success FROM blocks WHERE block_id='old:b1';";
            object? success = query.ExecuteScalar();
            Assert.True(success == null || success == DBNull.Value);
        }
    }
}
