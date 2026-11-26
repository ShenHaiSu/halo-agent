#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>会话树删除可靠性验收：备份、原子事务、锁、清理残留与桥接兼容。</summary>
public sealed class SessionDeletionReliabilityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionRepository _repository;
    private readonly SessionOperationCoordinator _coordinator = new();
    private readonly WorkspaceLifecycleGate _gate = new();
    private readonly WorkspaceSessionService _service;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _root;

    public SessionDeletionReliabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"SessionDeletion_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"SessionDeletionBootstrap_{Guid.NewGuid():N}"
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
        string projectDir = Path.Combine(_tempRoot, "Projects", "DeletionReliability");
        Directory.CreateDirectory(projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(projectDir, "删除可靠性测试")
            .GetAwaiter()
            .GetResult();
        _root = _service
            .GetSessionsByWorkspaceAsync(_workspace.Id)
            .GetAwaiter()
            .GetResult()
            .Single();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言。
        }
        try
        {
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言。
        }
    }

    [Fact]
    public async Task SingleSessionDeletionRemovesFourTablesDirectoryAndIndexAndCreatesUsableBackup()
    {
        var other = await _service.CreateSessionAsync(_workspace.Id, "保留会话");
        await SeedMessageAsync(_root.Id, "待删除消息");
        await SeedMessageAsync(other.Id, "保留消息");

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.True(
            result.Outcome == SessionDeletionOutcome.Deleted,
            string.Join(Environment.NewLine, result.Warnings)
        );
        Assert.True(result.DestructiveCommitCompleted);
        Assert.Equal(SessionDeletionStage.Completed, result.Database);
        Assert.Equal(SessionDeletionStage.Completed, result.Directories);
        Assert.Equal(SessionDeletionStage.Completed, result.Index);
        Assert.Equal(new[] { _root.Id }, result.DeletedSessionIds);
        Assert.Empty(result.ResidualSessionIds);
        Assert.Equal("history.db.bak", result.BackupFileName);
        Assert.True(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));

        foreach (string table in new[] { "messages", "blocks", "turns", "sessions" })
            Assert.Equal(
                0,
                await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), table, _root.Id)
            );
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", other.Id)
        );
        Assert.True(
            await CountRowsAsync(
                _storage.GetWorkspaceBackupPath(_workspace.Id),
                "messages",
                _root.Id
            ) > 0
        );
        Assert.False(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, _root.Id)));
        Assert.DoesNotContain(
            (await _repository.LoadSessionsIndexStrictAsync(_workspace.Id)),
            item => string.Equals(item.Id, _root.Id, StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, other.Id)));
    }

    [Fact]
    public async Task MultiLevelTreeDeletionIncludesEveryDescendant()
    {
        var child = await CreateChildAsync(_root.Id, "child");
        var grandchild = await CreateChildAsync(child.Id, "grandchild");
        var other = await _service.CreateSessionAsync(_workspace.Id, "保留会话");
        await SeedMessageAsync(_root.Id, "root");
        await SeedMessageAsync(child.Id, "child");
        await SeedMessageAsync(grandchild.Id, "grandchild");
        await SeedMessageAsync(other.Id, "other");

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(
            new[] { _root.Id, child.Id, grandchild.Id }.OrderBy(
                id => id,
                StringComparer.OrdinalIgnoreCase
            ),
            result.DeletedSessionIds
        );
        foreach (SessionMetadata session in new[] { _root, child, grandchild })
        {
            Assert.Equal(
                0,
                await CountRowsAsync(
                    _storage.GetWorkspaceDbPath(_workspace.Id),
                    "messages",
                    session.Id
                )
            );
            Assert.False(
                Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, session.Id))
            );
        }
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", other.Id)
        );
        Assert.Single(await _repository.LoadSessionsIndexStrictAsync(_workspace.Id));
    }

    [Fact]
    public async Task RepeatedDeleteReturnsNotFoundWithoutReplacingBackup()
    {
        await SeedMessageAsync(_root.Id, "message");
        SessionDeletionResult first = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );
        Assert.Equal(SessionDeletionOutcome.Deleted, first.Outcome);
        string backupPath = _storage.GetWorkspaceBackupPath(_workspace.Id);
        byte[] backupHash = SHA256.HashData(File.ReadAllBytes(backupPath));

        SessionDeletionResult second = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.NotFound, second.Outcome);
        Assert.False(second.DestructiveCommitCompleted);
        Assert.Equal("SESSION_NOT_FOUND", second.ErrorCode);
        Assert.Equal(backupHash, SHA256.HashData(File.ReadAllBytes(backupPath)));
    }

    [Fact]
    public void TreeCollectionDeduplicatesIdsCaseInsensitivelyAndOrdersDirectoriesDeepFirst()
    {
        var sessions = new List<SessionMetadata>
        {
            new() { Id = "sess-root" },
            new() { Id = "sess-child", ParentSessionId = "sess-root" },
            new() { Id = "SESS-GRANDCHILD", ParentSessionId = "SESS-CHILD" },
            new() { Id = "sess-grandchild", ParentSessionId = "sess-child" },
        };

        SessionDeletionTreeResult tree = SessionDeletionTree.Collect("SESS-ROOT", sessions);

        Assert.Null(tree.ErrorCode);
        Assert.False(tree.RootMissing);
        Assert.Equal(3, tree.Nodes.Count);
        Assert.Equal(
            new[] { "SESS-GRANDCHILD", "sess-child", "sess-root" },
            SessionDeletionTree
                .OrderDirectoriesDeepFirst(tree.Nodes)
                .Select(node => node.SessionId)
                .ToArray()
        );
    }

    [Fact]
    public async Task SecondTableFailureRollsBackAllFourTablesAndLeavesFilesUntouched()
    {
        var child = await CreateChildAsync(_root.Id, "child");
        await SeedPartitionAsync(_root.Id, "root");
        await SeedPartitionAsync(child.Id, "child");
        ExecuteSql(
            "CREATE TRIGGER fail_blocks_delete BEFORE DELETE ON blocks BEGIN "
                + "SELECT RAISE(ABORT, 'block-delete-fault'); END;"
        );

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Failed, result.Outcome);
        Assert.False(result.DestructiveCommitCompleted);
        Assert.Equal("SESSION_DATABASE_DELETE_FAILED", result.ErrorCode);
        Assert.Equal(SessionDeletionStage.Failed, result.Database);
        Assert.Equal(SessionDeletionStage.NotRequested, result.Directories);
        Assert.Equal(SessionDeletionStage.NotRequested, result.Index);
        Assert.True(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
        foreach (SessionMetadata session in new[] { _root, child })
        {
            foreach (string table in new[] { "messages", "blocks", "turns", "sessions" })
                Assert.True(
                    await CountRowsAsync(
                        _storage.GetWorkspaceDbPath(_workspace.Id),
                        table,
                        session.Id
                    ) > 0
                );
            Assert.True(
                Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, session.Id))
            );
        }
        Assert.Equal(2, (await _repository.LoadSessionsIndexStrictAsync(_workspace.Id)).Count);
    }

    [Fact]
    public async Task ChildDirectoryFailureReturnsPartialWithResidualId()
    {
        var child = await CreateChildAsync(_root.Id, "child");
        await SeedMessageAsync(_root.Id, "root");
        await SeedMessageAsync(child.Id, "child");
        string childMeta = _repository.GetSessionMetaPath(_workspace.Id, child.Id);
        using var lockedFile = new FileStream(
            childMeta,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Partial, result.Outcome);
        Assert.True(result.DestructiveCommitCompleted);
        Assert.Equal(SessionDeletionStage.Completed, result.Database);
        Assert.Equal(SessionDeletionStage.Failed, result.Directories);
        Assert.Equal(SessionDeletionStage.Completed, result.Index);
        Assert.Equal(new[] { child.Id }, result.ResidualSessionIds);
        Assert.Equal(
            0,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", child.Id)
        );
        Assert.Equal(
            0,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
        Assert.True(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, child.Id)));
        Assert.False(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, _root.Id)));
        Assert.Empty(await _repository.LoadSessionsIndexStrictAsync(_workspace.Id));
    }

    [Fact]
    public async Task DirectoryOnlyResidualCanBeRetriedByResidualId()
    {
        var child = await CreateChildAsync(_root.Id, "child");
        string childMeta = _repository.GetSessionMetaPath(_workspace.Id, child.Id);
        SessionDeletionResult first;
        using (
            var lockedFile = new FileStream(
                childMeta,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            )
        )
        {
            first = await _service.DeleteSessionTreeAsync(_workspace.Id, _root.Id);
        }
        Assert.Equal(SessionDeletionOutcome.Partial, first.Outcome);
        Assert.Equal(new[] { child.Id }, first.ResidualSessionIds);
        Assert.Empty(await _repository.LoadSessionsIndexStrictAsync(_workspace.Id));

        SessionDeletionResult retry = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            child.Id
        );

        Assert.Equal(SessionDeletionOutcome.Deleted, retry.Outcome);
        Assert.True(retry.DestructiveCommitCompleted);
        Assert.False(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, child.Id)));
    }

    [Fact]
    public async Task IndexWriteFailureReturnsPartialAfterDatabaseAndDirectoriesCommit()
    {
        await SeedMessageAsync(_root.Id, "root");
        string indexPath = _repository.GetSessionsIndexPath(_workspace.Id);
        FileAttributes original = File.GetAttributes(indexPath);
        File.SetAttributes(indexPath, original | FileAttributes.ReadOnly);
        try
        {
            SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
                _workspace.Id,
                _root.Id
            );

            Assert.Equal(SessionDeletionOutcome.Partial, result.Outcome);
            Assert.True(result.DestructiveCommitCompleted);
            Assert.Equal(SessionDeletionStage.Completed, result.Database);
            Assert.Equal(SessionDeletionStage.Completed, result.Directories);
            Assert.Equal(SessionDeletionStage.Failed, result.Index);
            Assert.Equal(new[] { _root.Id }, result.ResidualSessionIds);
            Assert.Equal(
                0,
                await CountRowsAsync(
                    _storage.GetWorkspaceDbPath(_workspace.Id),
                    "messages",
                    _root.Id
                )
            );
            Assert.False(
                Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, _root.Id))
            );
            Assert.Contains(
                await _repository.LoadSessionsIndexStrictAsync(_workspace.Id),
                item => item.Id == _root.Id
            );
        }
        finally
        {
            File.SetAttributes(indexPath, original);
        }
    }

    [Fact]
    public async Task BackupFailureLeavesWorkspaceFilesUnchanged()
    {
        await SeedMessageAsync(_root.Id, "root");
        Dictionary<string, byte[]> before = SnapshotWorkspaceFiles();
        string backupPath = _storage.GetWorkspaceBackupPath(_workspace.Id);
        Directory.CreateDirectory(backupPath);

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Failed, result.Outcome);
        Assert.False(result.DestructiveCommitCompleted);
        Assert.Equal("BACKUP_FAILED", result.ErrorCode);
        Assert.Equal(SessionDeletionStage.NotRequested, result.Database);
        Assert.Null(result.BackupFileName);
        Assert.Equal(before, SnapshotWorkspaceFiles());
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
        Assert.True(Directory.Exists(_repository.GetSessionDirectory(_workspace.Id, _root.Id)));
    }

    [Fact]
    public async Task CancellationFailureLeavesDataUntouched()
    {
        await SeedMessageAsync(_root.Id, "root");
        var service = new WorkspaceSessionService(
            _repository,
            _coordinator,
            _gate,
            _ => throw new InvalidOperationException("cancel-fault")
        );

        SessionDeletionResult result = await service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Failed, result.Outcome);
        Assert.Equal("CANCEL_FAILED", result.ErrorCode);
        Assert.False(result.DestructiveCommitCompleted);
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
    }

    [Fact]
    public async Task CycleIsRejectedBeforeBackup()
    {
        var child = await CreateChildAsync(_root.Id, "child");
        await SeedMessageAsync(_root.Id, "root");
        var sessions = await _repository.LoadSessionsIndexStrictAsync(_workspace.Id);
        sessions.Single(item => item.Id == _root.Id).ParentSessionId = child.Id;
        sessions.Single(item => item.Id == child.Id).ParentSessionId = _root.Id;
        await _repository.SaveSessionsIndexAsync(_workspace.Id, sessions);

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Failed, result.Outcome);
        Assert.Equal("SESSION_TREE_CYCLE", result.ErrorCode);
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
        Assert.Equal(2, (await _repository.LoadSessionsIndexStrictAsync(_workspace.Id)).Count);
    }

    [Fact]
    public async Task BusySessionIsRejectedBeforeBackup()
    {
        await SeedMessageAsync(_root.Id, "root");
        Assert.True(
            _coordinator.TryAcquire(
                _workspace.Id,
                _root.Id,
                SessionOperationKind.Agent,
                out var busyLease
            )
        );
        using (busyLease)
        {
            SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
                _workspace.Id,
                _root.Id
            );
            Assert.Equal(SessionDeletionOutcome.Failed, result.Outcome);
            Assert.Equal("SESSION_BUSY", result.ErrorCode);
        }
        Assert.False(File.Exists(_storage.GetWorkspaceBackupPath(_workspace.Id)));
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
    }

    [Fact]
    public async Task DeletionBlocksAppendAndCreationUntilCommit()
    {
        await SeedMessageAsync(_root.Id, "root");
        SemaphoreSlim appendLock = _service.GetSessionAppendLock(_root.Id);
        await appendLock.WaitAsync();
        Task<SessionDeletionResult> deletion = _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );
        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => _gate.IsExclusiveHeld(_workspace.Id),
                    TimeSpan.FromSeconds(2)
                )
            );
            await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    _service.AppendSessionMessageAsync(
                        _workspace.Id,
                        _root.Id,
                        UnifiedChatMessage.User("must-not-resurrect")
                    )
            );
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.CreateSessionAsync(_workspace.Id, "must-not-create")
            );
        }
        finally
        {
            appendLock.Release();
        }

        SessionDeletionResult result = await deletion;
        Assert.Equal(SessionDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(
            0,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
        Assert.Empty(await _repository.LoadSessionsIndexStrictAsync(_workspace.Id));
    }

    [Fact]
    public async Task AgentStartIsRejectedWhileWorkspaceDeletionLeaseIsHeld()
    {
        var facade = new HostBridgeFacade(null, _storage, workspaceSessionService: _service);
        using IDisposable exclusive = (
            await _gate.TryAcquireExclusiveAsync(_workspace.Id, TimeSpan.FromSeconds(1))
        )!;

        var response = await facade.StartAgentTurn(_workspace.Id, _root.Id, "must-not-start");

        Assert.False(response.Ok);
        Assert.Equal("WORKSPACE_BUSY", response.Code);
    }

    [Fact]
    public async Task ReparsePointIsSkippedWithoutTouchingItsTarget()
    {
        await SeedMessageAsync(_root.Id, "root");
        string sessionDir = _repository.GetSessionDirectory(_workspace.Id, _root.Id);
        string externalDir = Path.Combine(_tempRoot, "ExternalTarget");
        Directory.CreateDirectory(externalDir);
        string externalFile = Path.Combine(externalDir, "keep.txt");
        File.WriteAllText(externalFile, "keep", Encoding.UTF8);
        string junction = Path.Combine(sessionDir, "linked");
        JunctionPoint.Create(junction, externalDir);

        SessionDeletionResult result = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );

        Assert.Equal(SessionDeletionOutcome.Partial, result.Outcome);
        Assert.True(result.DestructiveCommitCompleted);
        Assert.Equal(SessionDeletionStage.Failed, result.Directories);
        Assert.Equal(new[] { _root.Id }, result.ResidualSessionIds);
        Assert.Contains(result.Warnings, warning => warning.Contains("REPARSE_POINT_SKIPPED"));
        Assert.Equal(
            0,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
        Assert.True(Directory.Exists(sessionDir));
        Assert.True(File.Exists(externalFile));
        Assert.Equal("keep", File.ReadAllText(externalFile));
    }

    [Fact]
    public async Task InvalidPathAndCorruptIndexAreRejectedWithoutDeletion()
    {
        await SeedMessageAsync(_root.Id, "root");
        SessionDeletionResult invalid = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            "../outside"
        );
        Assert.Equal("INVALID_SESSION", invalid.ErrorCode);

        string indexPath = _repository.GetSessionsIndexPath(_workspace.Id);
        File.WriteAllText(indexPath, "{broken", Encoding.UTF8);
        SessionDeletionResult corrupt = await _service.DeleteSessionTreeAsync(
            _workspace.Id,
            _root.Id
        );
        Assert.Equal(SessionDeletionOutcome.Failed, corrupt.Outcome);
        Assert.Equal("SESSION_DELETE_ERROR", corrupt.ErrorCode);
        Assert.False(corrupt.DestructiveCommitCompleted);
        Assert.Equal(
            1,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "messages", _root.Id)
        );
    }

    [Fact]
    public async Task EmptyBatchDoesNotCreateDatabase()
    {
        var store = new WorkspaceSessionStore(_storage);
        await store.DeleteSessionPartitionsAsync("ws-empty", Array.Empty<string>());
        Assert.False(File.Exists(_storage.GetWorkspaceDbPath("ws-empty")));
    }

    [Fact]
    public async Task BridgeV2IsStructuredAndLegacyEntryUsesStrictOutcome()
    {
        var facade = new HostBridgeFacade(null, _storage, workspaceSessionService: _service);
        await SeedMessageAsync(_root.Id, "root");

        var response = await facade.DeleteSessionV2(_workspace.Id, _root.Id);
        Assert.True(response.Ok);
        Assert.NotNull(response.Data);
        Assert.Equal("deleted", response.Data!.Outcome);

        var other = await _service.CreateSessionAsync(_workspace.Id, "legacy");
        var legacy = await facade.DeleteSession(_workspace.Id, other.Id);
        Assert.True(legacy.Ok);
        Assert.True(legacy.Data);
    }

    [Fact]
    public async Task LegacyEntryReturnsFalseForPartialDeletion()
    {
        var facade = new HostBridgeFacade(null, _storage, workspaceSessionService: _service);
        string metaPath = _repository.GetSessionMetaPath(_workspace.Id, _root.Id);
        using var lockedFile = new FileStream(
            metaPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        var response = await facade.DeleteSession(_workspace.Id, _root.Id);

        Assert.True(response.Ok);
        Assert.False(response.Data);
        Assert.Equal(
            0,
            await CountRowsAsync(_storage.GetWorkspaceDbPath(_workspace.Id), "sessions", _root.Id)
        );
    }

    private async Task<SessionMetadata> CreateChildAsync(string parentId, string title)
    {
        return await _service.CreateSubSessionAsync(
            _workspace.Id,
            title,
            null,
            null,
            parentId,
            "sub",
            title,
            null,
            null
        );
    }

    private async Task SeedMessageAsync(string sessionId, string text)
    {
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            sessionId,
            UnifiedChatMessage.User(text)
        );
    }

    private async Task SeedPartitionAsync(string sessionId, string text)
    {
        var message = UnifiedChatMessage.User(text);
        string turnId = "turn-" + Guid.NewGuid().ToString("N");
        var block = new TimelineBlock
        {
            BlockId = "block-" + Guid.NewGuid().ToString("N"),
            TurnId = turnId,
            Sequence = 1,
            Kind = TimelineBlockKind.Text,
            Tag = TimelineBlockTag.Final,
            CharCount = text.Length,
            Preview = text,
            Content = text,
        };
        var summary = new TurnSummary
        {
            TurnId = turnId,
            TurnSeq = 1,
            UserMessageId = message.Id,
            UserPreview = text,
            UserContent = text,
            FinalBlockId = block.BlockId,
            FinalPreview = text,
            FinalContent = text,
            Blocks = 1,
            Chars = text.Length,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        await new WorkspaceSessionStore(_storage).AppendTurnAsync(
            _workspace.Id,
            sessionId,
            new[] { message },
            new[] { block },
            summary
        );
    }

    private async Task<long> CountRowsAsync(string dbPath, string table, string sessionId)
    {
        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private void ExecuteSql(string sql)
    {
        using var conn = new SessionHistoryDb(_storage).OpenWorkspace(_workspace.Id);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, byte[]> SnapshotWorkspaceFiles()
    {
        string workspaceDir = _repository.GetWorkspaceDirectory(_workspace.Id);
        return Directory
            .GetFiles(workspaceDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(workspaceDir, path),
                path => SHA256.HashData(File.ReadAllBytes(path)),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static class JunctionPoint
    {
        public static void Create(string link, string target)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process =
                Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start mklink.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"mklink failed: {stdout} {stderr}");
        }
    }
}
