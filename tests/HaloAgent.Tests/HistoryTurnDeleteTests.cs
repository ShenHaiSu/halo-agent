#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 整轮删除验收：投影三表单事务、边界锚点、备份门禁、故障注入回滚、愈合与并发互斥。
/// </summary>
public sealed class HistoryTurnDeleteTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionRepository _repository;
    private readonly SessionOperationCoordinator _coordinator = new();
    private readonly WorkspaceLifecycleGate _gate = new();
    private readonly WorkspaceSessionService _service;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public HistoryTurnDeleteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"TurnDeletion_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"TurnDeletionBootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storage);
        _service = new WorkspaceSessionService(_repository, _coordinator, _gate);
        string projectDir = Path.Combine(_tempRoot, "Projects", "TurnDeletion");
        Directory.CreateDirectory(projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(projectDir, "整轮删除测试")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "整轮删除会话").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (string root in new[] { _tempRoot, _bootstrapRoot })
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    private readonly record struct SeededTurn(
        string TurnId,
        string UserId,
        string AssistantId,
        string ToolId,
        string CallId
    );

    private async Task<SeededTurn> SeedTurnAsync(
        string sessionId,
        string label,
        long turnSeq,
        bool withToolCall = false,
        bool withToolReply = false
    )
    {
        var user = UnifiedChatMessage.User("提问-" + label);
        string callId = "call-" + label;
        var assistant = withToolCall
            ? UnifiedChatMessage.Assistant(
                "回复-" + label,
                new List<UnifiedToolCall> { new(callId, null, "read_file", "{}") }
            )
            : UnifiedChatMessage.Assistant("回复-" + label);
        var messages = new List<UnifiedChatMessage> { user, assistant };
        string toolId = string.Empty;
        if (withToolReply)
        {
            var tool = UnifiedChatMessage.Tool(callId, "回包-" + label, "read_file");
            toolId = tool.Id;
            messages.Add(tool);
        }

        string turnId = "uturn-" + user.Id;
        var block = new TimelineBlock
        {
            BlockId = "block-" + Guid.NewGuid().ToString("N"),
            TurnId = turnId,
            Sequence = 1,
            Kind = TimelineBlockKind.Text,
            Tag = TimelineBlockTag.Final,
            CharCount = 4,
            Preview = "回复-" + label,
            Content = "回复-" + label,
        };
        var summary = new TurnSummary
        {
            TurnId = turnId,
            TurnSeq = turnSeq,
            UserMessageId = user.Id,
            AssistantMessageId = assistant.Id,
            UserPreview = "提问-" + label,
            UserContent = "提问-" + label,
            FinalBlockId = block.BlockId,
            FinalPreview = "回复-" + label,
            FinalContent = "回复-" + label,
            Blocks = 1,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        await new WorkspaceSessionStore(_storage)
            .AppendTurnAsync(_workspace.Id, sessionId, messages, new[] { block }, summary)
            .ConfigureAwait(false);
        return new SeededTurn(turnId, user.Id, assistant.Id, toolId, callId);
    }

    private async Task<long> CountAsync(string table, string column, string value)
    {
        using var conn = SessionHistoryDb.OpenConnection(
            _storage.GetWorkspaceDbPath(_workspace.Id)
        );
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $value;";
        cmd.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private async Task<List<string>> ContentsAsync(string sessionId)
    {
        var messages = await _repository
            .GetMessagesAsync(_workspace.Id, sessionId)
            .ConfigureAwait(false);
        return messages.Select(m => m.Content ?? string.Empty).ToList();
    }

    private void ExecuteSql(string sql)
    {
        using var conn = new SessionHistoryDb(_storage).OpenWorkspace(_workspace.Id);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task DeleteMiddleTurnRemovesOnlyItsProjectionAndKeepsNeighbors()
    {
        var first = await SeedTurnAsync(_session.Id, "一", 1);
        var middle = await SeedTurnAsync(_session.Id, "二", 2);
        var last = await SeedTurnAsync(_session.Id, "三", 3);

        var result = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            middle.AssistantId
        );

        Assert.True(result.Success);
        Assert.True(result.DestructiveCommitCompleted);
        Assert.Equal(middle.TurnId, result.DeletedTurnId);
        Assert.Equal(2, result.DeletedMessages);
        Assert.Equal(4, result.KeptMessages);
        Assert.Equal("history.db.bak", result.BackupFileName);
        Assert.True(result.ProjectionDeleted);
        Assert.Null(result.MetadataWarning);
        Assert.Equal(new[] { "提问-一", "回复-一", "提问-三", "回复-三" }, await ContentsAsync(_session.Id));
        Assert.Equal(0, await CountAsync("turns", "turn_id", middle.TurnId));
        Assert.Equal(0, await CountAsync("blocks", "turn_id", middle.TurnId));
        Assert.Equal(1, await CountAsync("turns", "turn_id", first.TurnId));
        Assert.Equal(1, await CountAsync("blocks", "turn_id", first.TurnId));
        Assert.Equal(1, await CountAsync("turns", "turn_id", last.TurnId));
        Assert.Equal(1, await CountAsync("blocks", "turn_id", last.TurnId));
        Assert.True(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
    }

    [Fact]
    public async Task DeleteFirstThenLastTurnLeavesEmptyProjectionButKeepsSession()
    {
        var first = await SeedTurnAsync(_session.Id, "前", 1);
        var last = await SeedTurnAsync(_session.Id, "后", 2);

        var firstResult = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            first.UserId
        );
        Assert.True(firstResult.Success);
        Assert.Equal(new[] { "提问-后", "回复-后" }, await ContentsAsync(_session.Id));

        var lastResult = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            last.AssistantId
        );
        Assert.True(lastResult.Success);
        Assert.Empty(await ContentsAsync(_session.Id));
        Assert.Equal(0, await CountAsync("turns", "session_id", _session.Id));
        Assert.Equal(0, await CountAsync("blocks", "session_id", _session.Id));
        Assert.NotNull(await _repository.LoadSessionMetaAsync(_workspace.Id, _session.Id));
    }

    [Fact]
    public async Task UserAndAssistantAnchorsResolveTheSameTurn()
    {
        var other = await _service.CreateSessionAsync(_workspace.Id, "锚点对照会话");
        var turnA = await SeedTurnAsync(_session.Id, "控制", 1);
        var turnB = await SeedTurnAsync(other.Id, "对照", 1);

        var byUser = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            turnA.UserId
        );
        var byAssistant = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            other.Id,
            turnB.AssistantId
        );

        Assert.True(byUser.Success);
        Assert.True(byAssistant.Success);
        Assert.Equal(turnA.TurnId, byUser.DeletedTurnId);
        Assert.Equal(turnB.TurnId, byAssistant.DeletedTurnId);
        Assert.Equal(byUser.DeletedMessages, byAssistant.DeletedMessages);
        Assert.Equal(byUser.KeptMessages, byAssistant.KeptMessages);
        Assert.Empty(await ContentsAsync(_session.Id));
        Assert.Empty(await ContentsAsync(other.Id));
    }

    [Fact]
    public async Task ToolAnchorAndMissingAnchorAreRejectedBeforeBackup()
    {
        var turn = await SeedTurnAsync(
            _session.Id,
            "工具",
            1,
            withToolCall: true,
            withToolReply: true
        );
        string before = string.Join("|", await ContentsAsync(_session.Id));

        var toolResult = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            turn.ToolId
        );
        var missingResult = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            "msg-not-present"
        );

        Assert.Equal("INVALID_ANCHOR_ROLE", toolResult.ErrorCode);
        Assert.False(toolResult.Success);
        Assert.Equal("ANCHOR_NOT_FOUND", missingResult.ErrorCode);
        Assert.False(missingResult.Success);
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
        Assert.Equal(before, string.Join("|", await ContentsAsync(_session.Id)));
        Assert.Equal(1, await CountAsync("turns", "turn_id", turn.TurnId));
    }

    [Fact]
    public async Task BackupFailureReturnsZeroDeletion()
    {
        var turn = await SeedTurnAsync(_session.Id, "备份", 1);
        Directory.CreateDirectory(_storage.GetWorkspaceBackupPath(_workspace.Id));

        var result = await _service.DeleteSessionTurnAsync(_workspace.Id, _session.Id, turn.UserId);

        Assert.False(result.Success);
        Assert.Equal("BACKUP_FAILED", result.ErrorCode);
        Assert.False(result.DestructiveCommitCompleted);
        Assert.Equal(2, (await ContentsAsync(_session.Id)).Count);
        Assert.Equal(1, await CountAsync("turns", "turn_id", turn.TurnId));
        Assert.Equal(1, await CountAsync("blocks", "turn_id", turn.TurnId));
    }

    [Fact]
    public async Task BlockDeleteFailureRollsBackMessageRewriteAndTurnRows()
    {
        var turn = await SeedTurnAsync(_session.Id, "块故障", 1);
        var other = await SeedTurnAsync(_session.Id, "保留", 2);
        ExecuteSql(
            "CREATE TRIGGER fail_blocks_delete BEFORE DELETE ON blocks BEGIN "
                + "SELECT RAISE(ABORT, 'block-delete-fault'); END;"
        );

        var result = await _service.DeleteSessionTurnAsync(_workspace.Id, _session.Id, turn.UserId);

        Assert.False(result.Success);
        Assert.Equal("DELETE_COMMIT_FAILED", result.ErrorCode);
        Assert.False(result.DestructiveCommitCompleted);
        Assert.Equal(
            new[] { "提问-块故障", "回复-块故障", "提问-保留", "回复-保留" },
            await ContentsAsync(_session.Id)
        );
        Assert.Equal(1, await CountAsync("turns", "turn_id", turn.TurnId));
        Assert.Equal(1, await CountAsync("blocks", "turn_id", turn.TurnId));
        Assert.Equal(1, await CountAsync("turns", "turn_id", other.TurnId));
        Assert.True(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
    }

    [Fact]
    public async Task TurnDeleteFailureRollsBackAllFourWrites()
    {
        var turn = await SeedTurnAsync(_session.Id, "轮故障", 1);
        ExecuteSql(
            "CREATE TRIGGER fail_turns_delete BEFORE DELETE ON turns BEGIN "
                + "SELECT RAISE(ABORT, 'turn-delete-fault'); END;"
        );

        var result = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            turn.AssistantId
        );

        Assert.Equal("DELETE_COMMIT_FAILED", result.ErrorCode);
        Assert.Equal(2, (await ContentsAsync(_session.Id)).Count);
        Assert.Equal(1, await CountAsync("turns", "turn_id", turn.TurnId));
        Assert.Equal(1, await CountAsync("blocks", "turn_id", turn.TurnId));
    }

    [Fact]
    public async Task DeletedTurnHealsUnfinishedToolCallInRemainingHistory()
    {
        var doomed = await SeedTurnAsync(
            _session.Id,
            "已删",
            1,
            withToolCall: true,
            withToolReply: true
        );
        var kept = await SeedTurnAsync(_session.Id, "留存", 2, withToolCall: true);

        var result = await _service.DeleteSessionTurnAsync(
            _workspace.Id,
            _session.Id,
            doomed.UserId
        );

        Assert.True(result.Success);
        Assert.Equal(1, result.SyntheticAppended);
        Assert.Equal(3, result.KeptMessages);
        var messages = await _repository.GetMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(3, messages.Count);
        Assert.Equal(kept.CallId, messages[1].ToolCalls![0].CallId);
        Assert.Equal("tool", messages[2].Role);
        Assert.Equal(kept.CallId, messages[2].ToolCallId);
        Assert.StartsWith(
            WorkspaceSessionService.SyntheticCancelledToolPrefix,
            messages[2].Content
        );
    }

    [Fact]
    public async Task MetadataFailureKeepsCommittedDeletionAndReportsWarning()
    {
        var turn = await SeedTurnAsync(_session.Id, "元数据", 1);
        string indexPath = _repository.GetSessionsIndexPath(_workspace.Id);
        FileAttributes original = File.GetAttributes(indexPath);
        File.SetAttributes(indexPath, original | FileAttributes.ReadOnly);
        try
        {
            var result = await _service.DeleteSessionTurnAsync(
                _workspace.Id,
                _session.Id,
                turn.UserId
            );

            Assert.True(result.Success);
            Assert.True(result.DestructiveCommitCompleted);
            Assert.Contains("METADATA_REFRESH_FAILED", result.MetadataWarning);
            Assert.Empty(await ContentsAsync(_session.Id));
            Assert.Equal(0, await CountAsync("turns", "turn_id", turn.TurnId));
        }
        finally
        {
            File.SetAttributes(indexPath, original);
        }
    }

    [Fact]
    public async Task BridgeReturnsStructuredResultAndStableErrorCode()
    {
        var facade = new HostBridgeFacade(null, _storage, workspaceSessionService: _service);
        var turn = await SeedTurnAsync(_session.Id, "桥接", 1);

        var success = await facade.DeleteSessionTurn(_workspace.Id, _session.Id, turn.UserId);
        Assert.True(success.Ok);
        Assert.NotNull(success.Data);
        Assert.True(success.Data!.DestructiveCommitCompleted);
        Assert.Equal(turn.TurnId, success.Data.DeletedTurnId);

        var failure = await facade.DeleteSessionTurn(_workspace.Id, _session.Id, turn.UserId);
        Assert.False(failure.Ok);
        Assert.Equal("ANCHOR_NOT_FOUND", failure.Code);
    }

    [Fact]
    public async Task HeldHistoryLeaseRejectsConcurrentDeletionUntilReleased()
    {
        var turn = await SeedTurnAsync(_session.Id, "并发", 1);
        Assert.True(
            _coordinator.TryAcquire(
                _workspace.Id,
                _session.Id,
                SessionOperationKind.History,
                out var held
            )
        );
        Assert.NotNull(held);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.DeleteSessionTurnAsync(_workspace.Id, _session.Id, turn.UserId)
            );
            Assert.Equal("HISTORY_OPERATION_IN_PROGRESS", ex.Message);
            Assert.Equal(2, (await ContentsAsync(_session.Id)).Count);
        }
        finally
        {
            held!.Dispose();
        }

        var retry = await _service.DeleteSessionTurnAsync(_workspace.Id, _session.Id, turn.UserId);
        Assert.True(retry.Success);
    }
}
