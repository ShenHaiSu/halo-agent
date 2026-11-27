#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>历史重放与分支的领域集成验收：整轮切面、锚点校验与严格回滚。</summary>
public sealed class WorkspaceSessionHistoryOperationsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public WorkspaceSessionHistoryOperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HistoryOpsTests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HistoryOpsBootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storage);
        _service = new WorkspaceSessionService(_repository);
        string projectDir = Path.Combine(_tempRoot, "Projects", "HistoryOps");
        Directory.CreateDirectory(projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(projectDir, "历史操作测试")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "历史操作会话").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言
        }
    }

    private async Task<List<UnifiedChatMessage>> SeedTwoTurnsAsync()
    {
        var messages = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("第一轮提问"),
            UnifiedChatMessage.Assistant("第一轮回复"),
            UnifiedChatMessage.User("第二轮提问"),
            UnifiedChatMessage.Assistant("第二轮回复"),
        };
        foreach (var message in messages)
            await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, message);
        return messages;
    }

    [Fact]
    public async Task UserAnchorTruncateKeepsPrefixAndReturnsReplayText()
    {
        var messages = await SeedTwoTurnsAsync();
        var result = await _service.TruncateSessionHistoryAsync(
            _workspace.Id,
            _session.Id,
            messages[2].Id,
            true
        );
        Assert.True(result.Success);
        Assert.True(result.DestructiveCommitCompleted);
        Assert.Equal("第二轮提问", result.ReplayText);
        Assert.Equal(2, result.KeptMessages);
        Assert.Equal(2, result.RemovedMessages);
        Assert.False(string.IsNullOrWhiteSpace(result.RecoveryToken));
        var kept = await _repository.GetMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public async Task AssistantAnchorTruncateRemovesWholeTurn()
    {
        var messages = await SeedTwoTurnsAsync();
        var result = await _service.TruncateSessionHistoryAsync(
            _workspace.Id,
            _session.Id,
            messages[3].Id,
            true
        );
        Assert.True(result.Success);
        Assert.Equal("第二轮提问", result.ReplayText);
        Assert.Equal(2, result.KeptMessages);
    }

    [Fact]
    public async Task MissingAnchorFailsWithoutModifyingHistory()
    {
        await SeedTwoTurnsAsync();
        var result = await _service.TruncateSessionHistoryAsync(
            _workspace.Id,
            _session.Id,
            "msg_missing",
            true
        );
        Assert.False(result.Success);
        Assert.Equal("ANCHOR_NOT_FOUND", result.ErrorCode);
        var messages = await _repository.GetMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(4, messages.Count);
    }

    [Fact]
    public async Task InvalidRoleFailsWithoutModifyingHistory()
    {
        await SeedTwoTurnsAsync();
        var tool = UnifiedChatMessage.Tool("call_x", "结果");
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, tool);
        var result = await _service.TruncateSessionHistoryAsync(
            _workspace.Id,
            _session.Id,
            tool.Id,
            true
        );
        Assert.False(result.Success);
        Assert.Equal("INVALID_ANCHOR_ROLE", result.ErrorCode);
        var messages = await _repository.GetMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(5, messages.Count);
    }

    [Fact]
    public async Task UserBranchExcludesAnchorAndKeepsSourceUnchanged()
    {
        var messages = await SeedTwoTurnsAsync();
        var created = await _service.BranchSessionAsync(
            _workspace.Id,
            _session.Id,
            messages[2].Id,
            includeAnchor: false
        );
        var targetMessages = await _repository.GetMessagesAsync(_workspace.Id, created.Id);
        Assert.Equal(2, targetMessages.Count);
        var sourceMessages = await _repository.GetMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(4, sourceMessages.Count);
        Assert.NotEqual(_session.Id, created.Id);
    }

    [Fact]
    public async Task AssistantBranchIncludesWholeTurn()
    {
        var messages = await SeedTwoTurnsAsync();
        var created = await _service.BranchSessionAsync(
            _workspace.Id,
            _session.Id,
            messages[3].Id,
            includeAnchor: true
        );
        var targetMessages = await _repository.GetMessagesAsync(_workspace.Id, created.Id);
        Assert.Equal(4, targetMessages.Count);
    }

    [Fact]
    public async Task AssistantAnchorWithoutIncludeAnchorIsRejected()
    {
        var messages = await SeedTwoTurnsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                _service.BranchSessionAsync(
                    _workspace.Id,
                    _session.Id,
                    messages[1].Id,
                    includeAnchor: false
                )
        );
    }

    [Fact]
    public async Task StrictRollbackVerifiesEveryStage()
    {
        var created = await _service.CreateSessionAsync(_workspace.Id, "待清理分支");
        var rollback = await _repository.RollbackCreatedSessionAsync(_workspace.Id, created.Id);
        Assert.True(rollback.PartitionDeleted);
        Assert.True(rollback.MetadataDeleted);
        Assert.True(rollback.DirectoryDeleted);
        Assert.True(rollback.IndexRemoved);
        Assert.False(rollback.HasFailure);
    }
}
