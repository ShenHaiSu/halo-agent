#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>工作区双模式验收：解绑保留数据、彻底删除只清两个根、路径安全与旧入口兼容。</summary>
public sealed class WorkspaceRemovalTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionRepository _repository;
    private readonly WorkspaceLifecycleGate _gate = new();
    private readonly WorkspaceSessionService _service;
    private readonly string _projectRoot;
    private readonly WorkspaceInfo _workspace;

    public WorkspaceRemovalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"WorkspaceRemoval_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"WorkspaceRemovalBootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storage);
        _service = new WorkspaceSessionService(
            _repository,
            new SessionOperationCoordinator(),
            _gate
        );
        _projectRoot = Path.Combine(_tempRoot, "Projects", "RemovalProject");
        Directory.CreateDirectory(_projectRoot);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectRoot, "移除测试")
            .GetAwaiter()
            .GetResult();
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

    private string HaloRoot => Path.Combine(_projectRoot, ".haloagent");
    private string SessionsRoot => _repository.GetWorkspaceDirectory(_workspace.Id);

    private void WriteEntry(string kind, string name)
    {
        string dir = Path.Combine(HaloRoot, kind, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.md"), "entry body", Encoding.UTF8);
    }

    [Fact]
    public async Task Unbind_EmptyDomain_RemovesIndexAndEmptyHaloAgent_KeepsSessions()
    {
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.True(Directory.Exists(HaloRoot));

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Unbind
        );

        Assert.Equal(WorkspaceRemovalOutcome.Completed, result.Outcome);
        Assert.True(result.WorkspaceUnbound);
        Assert.Equal(WorkspaceRemovalMode.Unbind, result.Mode);
        Assert.Equal(WorkspaceResourceStatus.NotRequested, result.SessionData);
        Assert.Equal(WorkspaceResourceStatus.Deleted, result.HaloAgent);
        Assert.Equal(ProjectDomainReasons.Empty, result.HaloAgentReason);
        Assert.DoesNotContain(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.True(File.Exists(_storage.GetWorkspaceDbPath(_workspace.Id)));
        Assert.False(Directory.Exists(HaloRoot));
    }

    [Fact]
    public async Task Unbind_WithSkillEntry_RetainsDomainAndReportsReason()
    {
        WriteEntry("skills", "useful-skill");

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Unbind
        );

        Assert.Equal(WorkspaceRemovalOutcome.CompletedWithWarnings, result.Outcome);
        Assert.True(result.WorkspaceUnbound);
        Assert.Equal(WorkspaceResourceStatus.Retained, result.HaloAgent);
        Assert.Equal(ProjectDomainReasons.ValidEntries, result.HaloAgentReason);
        Assert.Contains("HALO_AGENT_RETAINED", result.Warnings);
        Assert.True(Directory.Exists(HaloRoot));
        Assert.True(Directory.Exists(SessionsRoot));
    }

    [Fact]
    public async Task Unbind_WithMemoryEntryOrUnknownFile_RetainsDomain()
    {
        WriteEntry("memory", "useful-memory");
        File.WriteAllText(Path.Combine(HaloRoot, "notes.txt"), "custom", Encoding.UTF8);

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Unbind
        );

        Assert.Equal(WorkspaceResourceStatus.Retained, result.HaloAgent);
        Assert.Equal(ProjectDomainReasons.UnknownContent, result.HaloAgentReason);
        Assert.True(Directory.Exists(HaloRoot));
    }

    [Fact]
    public async Task Unbind_CorruptIndexWithRealEntry_DoesNotDeleteDomain()
    {
        WriteEntry("skills", "still-there");
        File.WriteAllText(
            Path.Combine(HaloRoot, "skills", "_index.json"),
            "{broken",
            Encoding.UTF8
        );

        ProjectDomainInspection inspection = SkillMemoryService.InspectProjectDomain(_projectRoot);

        Assert.False(inspection.SafeEmpty);
        Assert.True(inspection.HasValidEntries);
        Assert.Equal(ProjectDomainReasons.ValidEntries, inspection.Reason);

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Unbind
        );
        Assert.Equal(WorkspaceResourceStatus.Retained, result.HaloAgent);
        Assert.True(Directory.Exists(Path.Combine(HaloRoot, "skills", "still-there")));
    }

    [Fact]
    public async Task Unbind_MissingDomain_IsIdempotentSuccess()
    {
        Directory.Delete(HaloRoot, recursive: true);

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Unbind
        );

        Assert.Equal(WorkspaceRemovalOutcome.Completed, result.Outcome);
        Assert.Equal(WorkspaceResourceStatus.Deleted, result.HaloAgent);
        Assert.Equal(ProjectDomainReasons.NotFound, result.HaloAgentReason);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Purge_DeletesOnlyTwoRoots_AndKeepsOtherProjectFiles()
    {
        string keepFile = Path.Combine(_projectRoot, "keep-me.txt");
        File.WriteAllText(keepFile, "keep", Encoding.UTF8);
        WriteEntry("skills", "doomed-skill");

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Purge
        );

        Assert.Equal(WorkspaceRemovalOutcome.Completed, result.Outcome);
        Assert.True(result.WorkspaceUnbound);
        Assert.Equal(WorkspaceResourceStatus.Deleted, result.SessionData);
        Assert.Equal(WorkspaceResourceStatus.Deleted, result.HaloAgent);
        Assert.Empty(result.FailedPaths);
        Assert.False(Directory.Exists(SessionsRoot));
        Assert.False(Directory.Exists(HaloRoot));
        Assert.True(File.Exists(keepFile));
        Assert.Equal("keep", File.ReadAllText(keepFile));
        Assert.DoesNotContain(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );
    }

    [Fact]
    public async Task Purge_MissingTargets_IsIdempotentSuccess()
    {
        Directory.Delete(SessionsRoot, recursive: true);
        Directory.Delete(HaloRoot, recursive: true);

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Purge
        );

        Assert.Equal(WorkspaceResourceStatus.Deleted, result.SessionData);
        Assert.Equal(WorkspaceResourceStatus.Deleted, result.HaloAgent);
        Assert.Equal(WorkspaceRemovalOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task Purge_InvalidWorkspaceId_RejectsBeforeAnyDeletion()
    {
        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            "../outside",
            WorkspaceRemovalMode.Purge
        );

        Assert.Equal(WorkspaceRemovalOutcome.Failed, result.Outcome);
        Assert.Equal("INVALID_WORKSPACE_ID", result.ErrorCode);
        Assert.False(result.WorkspaceUnbound);
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.True(Directory.Exists(HaloRoot));
        Assert.Contains(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );
    }

    [Fact]
    public async Task Purge_CancelFailure_LeavesIndexAndFilesUntouched()
    {
        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Purge,
            default,
            _ => throw new InvalidOperationException("cancel refused")
        );

        Assert.Equal(WorkspaceRemovalOutcome.Failed, result.Outcome);
        Assert.Equal("CANCEL_FAILED", result.ErrorCode);
        Assert.False(result.WorkspaceUnbound);
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.Contains(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );
    }

    [Fact]
    public async Task Purge_ReparsePointInSessions_RejectsWithoutFollowingLink()
    {
        string outside = Path.Combine(_tempRoot, "ExternalTarget");
        Directory.CreateDirectory(outside);
        string keepFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(keepFile, "keep", Encoding.UTF8);
        JunctionPoint.Create(Path.Combine(SessionsRoot, "linked"), outside);

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Purge
        );

        Assert.Equal(WorkspaceRemovalOutcome.Failed, result.Outcome);
        Assert.Equal("REPARSE_POINT_UNSUPPORTED", result.ErrorCode);
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.True(Directory.Exists(HaloRoot));
        Assert.Equal("keep", File.ReadAllText(keepFile));
    }

    [Fact]
    public async Task Removal_WaitsForWorkspaceGate_AndCancelsWithoutDeletion()
    {
        using IDisposable shared = _gate.TryAcquireShared(_workspace.Id)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        WorkspaceRemovalResult result = await _service.RemoveWorkspaceV2Async(
            _workspace.Id,
            WorkspaceRemovalMode.Purge,
            timeout.Token
        );

        Assert.Equal(WorkspaceRemovalOutcome.Failed, result.Outcome);
        Assert.Equal("DELETE_CANCELLED", result.ErrorCode);
        Assert.True(Directory.Exists(SessionsRoot));
        Assert.Contains(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );
    }

    [Fact]
    public async Task BridgeV2_StructuredResultAndLegacyEntryUsesSafeUnbind()
    {
        var facade = new HostBridgeFacade(null, _storage, workspaceSessionService: _service);

        var invalid = await facade.RemoveWorkspaceV2(_workspace.Id, "destroy");
        Assert.False(invalid.Ok);
        Assert.Equal("WORKSPACE_VALIDATION", invalid.Code);

        var legacy = await facade.RemoveWorkspace(_workspace.Id);
        Assert.True(legacy.Ok);
        Assert.True(legacy.Data);
        Assert.True(Directory.Exists(SessionsRoot), "旧入口只解绑，不删会话目录");
        Assert.DoesNotContain(
            _workspace.Id,
            (await _service.GetAllWorkspacesAsync()).Select(ws => ws.Id)
        );

        var rebound = await _service.AddOrUpdateWorkspaceAsync(_projectRoot, "重新绑定");
        var purge = await facade.RemoveWorkspaceV2(rebound.Id, "purge");
        Assert.True(purge.Ok);
        Assert.NotNull(purge.Data);
        Assert.Equal("purge", purge.Data!.Mode);
        Assert.Equal("completed", purge.Data.Outcome);
        Assert.Equal("deleted", purge.Data.SessionData);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("nested/child")]
    [InlineData("nested\\child")]
    [InlineData("C:\\")]
    public void PathResolver_RejectsUnsafeSegments(string segment)
    {
        Assert.False(
            SessionDirectoryDeletion.TryResolveChildDirectory(_tempRoot, segment, out _),
            $"unsafe segment accepted: {segment}"
        );
    }

    [Theory]
    [InlineData("unbind", WorkspaceRemovalMode.Unbind)]
    [InlineData("PURGE", WorkspaceRemovalMode.Purge)]
    [InlineData(" purge ", WorkspaceRemovalMode.Purge)]
    public void ModeParser_NormalizesCaseAndWhitespace(string raw, WorkspaceRemovalMode expected)
    {
        Assert.True(WorkspaceRemovalModeParser.TryParse(raw, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("delete")]
    [InlineData("purge-all")]
    public void ModeParser_RejectsUnknownValues(string raw)
    {
        Assert.False(WorkspaceRemovalModeParser.TryParse(raw, out _));
    }

    [Fact]
    public async Task ConcurrentIndexMutations_PreserveBothResults()
    {
        string secondRoot = Path.Combine(_tempRoot, "Projects", "SecondProject");
        Directory.CreateDirectory(secondRoot);
        var second = await _service.AddOrUpdateWorkspaceAsync(secondRoot, "第二个工作区");
        string removedId = _workspace.Id;
        string keptId = second.Id;

        await Task.WhenAll(
            _repository.MutateWorkspacesIndexAsync(list =>
            {
                list.RemoveAll(ws =>
                    string.Equals(ws.Id, removedId, StringComparison.OrdinalIgnoreCase)
                );
                return null;
            }),
            _repository.MutateWorkspacesIndexAsync(list =>
            {
                var kept = list.First(ws =>
                    string.Equals(ws.Id, keptId, StringComparison.OrdinalIgnoreCase)
                );
                kept.Alias = "并发更新";
                return null;
            })
        );

        var remaining = await _repository.LoadWorkspacesIndexAsync();
        Assert.DoesNotContain(removedId, remaining.Select(ws => ws.Id));
        Assert.Contains(remaining, ws => ws.Id == keptId && ws.Alias == "并发更新");
    }

    [Fact]
    public void ResultDto_UsesStableStringEnums()
    {
        var dto = WorkspaceRemovalResultDto.FromResult(
            new WorkspaceRemovalResult
            {
                Outcome = WorkspaceRemovalOutcome.CompletedWithWarnings,
                Mode = WorkspaceRemovalMode.Purge,
                WorkspaceUnbound = true,
                SessionData = WorkspaceResourceStatus.Deleted,
                HaloAgent = WorkspaceResourceStatus.Failed,
                HaloAgentReason = "DELETE_FAILED",
                FailedPaths = new[] { "path-a" },
            }
        );

        Assert.Equal("completedWithWarnings", dto.Outcome);
        Assert.Equal("purge", dto.Mode);
        Assert.Equal("deleted", dto.SessionData);
        Assert.Equal("failed", dto.HaloAgent);
        Assert.Equal(new[] { "path-a" }, dto.FailedPaths);
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
