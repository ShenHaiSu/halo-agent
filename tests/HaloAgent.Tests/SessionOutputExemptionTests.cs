#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Safety;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 会话输出目录只读豁免：落盘全文可读回、跨会话仍隔离、写链不受影响。
/// </summary>
public sealed class SessionOutputExemptionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly string _projectDir;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly ToolRegistry _registry;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;
    private readonly ToolContext _context;

    public SessionOutputExemptionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloSessionOut_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloSessionOutBs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _registry = new ToolRegistry();

        _projectDir = Path.Combine(_tempRoot, "Projects", "SessionOutputCase");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "会话输出豁免用例")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "会话输出豁免会话").GetAwaiter().GetResult();

        _context = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
            DataRoot = _tempRoot,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            if (Directory.Exists(_bootstrapRoot))
            {
                Directory.Delete(_bootstrapRoot, recursive: true);
            }
        }
        catch
        {
            // 忽略测试临时目录清理异常
        }
    }

    private static string Args(object o) =>
        JsonSerializer.Serialize(
            o,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

    private static string BuildBigText(string marker)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 1500; i++)
        {
            sb.Append("line-")
                .Append(i.ToString("0000"))
                .Append("-abcdefghijklmnopqrstuvwxyz-0123456789");
            if (i == 750)
            {
                sb.Append('-').Append(marker);
            }

            sb.Append('\n');
        }

        sb.Append("tail-end\n");
        return sb.ToString();
    }

    private Task<ToolResult> Call(string name, object args, ToolContext? context = null) =>
        _registry.DispatchAsync(
            new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), name, Args(args)),
            context ?? _context
        );

    [Fact(DisplayName = "会话输出落盘后可用分页读回全文")]
    public async Task SpilledSessionOutputCanBeReadBack()
    {
        string big = BuildBigText("zebra-marker-8842");
        Assert.True(big.Length > ToolOutputGuard.MaxInlineChars);

        var (display, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "caseReadBack"
        );

        Assert.NotNull(spilled);
        Assert.True(File.Exists(spilled));
        Assert.Contains("read_file", display);

        int expectedTotal = big.Split('\n').Length;
        var first = await Call(
            "read_file",
            new
            {
                file_path = spilled,
                offset = 1,
                limit = 500
            }
        );
        Assert.True(first.Success, first.Error);
        Assert.Contains($"TotalLines: {expectedTotal}", first.Output);
        Assert.Contains("line-0001", first.Output);

        var middle = await Call(
            "read_file",
            new
            {
                file_path = spilled,
                offset = 1001,
                limit = 500
            }
        );
        Assert.True(middle.Success, middle.Error);
        Assert.Contains("line-1001", middle.Output);

        var tail = await Call(
            "read_file",
            new
            {
                file_path = spilled,
                offset = expectedTotal - 1,
                limit = 10
            }
        );
        Assert.True(tail.Success, tail.Error);
        Assert.Contains("tail-end", tail.Output);
    }

    [Fact(DisplayName = "会话输出落盘后可用检索与目录对账")]
    public async Task SpilledOutputSearchableByGrep()
    {
        const string marker = "zebra-marker-7719";
        string big = BuildBigText(marker);
        var (_, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "caseSearchable"
        );
        Assert.NotNull(spilled);

        var grep = await Call("grep_search", new { pattern = marker, file_path = spilled });
        Assert.True(grep.Success, grep.Error);
        Assert.Contains(marker, grep.Output);

        string dir = SessionOutputRoots.BuildSessionToolOutputsDir(
            _tempRoot,
            _workspace.Id,
            _session.Id
        );
        var list = await Call("list_dir", new { file_path = dir });
        Assert.True(list.Success, list.Error);
        Assert.Contains(Path.GetFileName(spilled), list.Output);
    }

    [Fact(DisplayName = "管线安全检查对本会话输出放行、无豁免仍拒绝")]
    public void PipelineSafetyAllowsSessionOutput()
    {
        string big = BuildBigText("zebra-marker-6601");
        var (_, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "casePipeline"
        );
        Assert.NotNull(spilled);

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["file_path"] = spilled! }
        );
        string[] roots = SessionOutputRoots.GetSessionReadAllowedRoots(_context);
        Assert.Single(roots);

        var allowed = ToolPipeline.CheckSafety("read_file", json, _projectDir, roots);
        Assert.True(allowed.Allowed);

        var denied = ToolPipeline.CheckSafety("read_file", json, _projectDir);
        Assert.False(denied.Allowed);
        Assert.Equal(PathSafetyValidator.Codes.OutsideWorkspace, denied.Code);
    }

    [Fact(DisplayName = "他会话与他工作区输出仍拒绝读取")]
    public async Task OtherSessionOutputStillDenied()
    {
        string big = BuildBigText("zebra-marker-5502");
        string otherSessionId = "other-" + Guid.NewGuid().ToString("N");
        var (_, otherSpilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            otherSessionId,
            "caseOtherSide"
        );
        Assert.NotNull(otherSpilled);

        var readOther = await Call("read_file", new { file_path = otherSpilled });
        Assert.False(readOther.Success);
        Assert.Contains(
            PathSafetyValidator.Codes.OutsideWorkspace,
            readOther.Error ?? string.Empty
        );
        Assert.DoesNotContain(_tempRoot, readOther.Error ?? string.Empty);

        string otherWorkspaceId = "ws-" + Guid.NewGuid().ToString("N");
        var (_, wsSpilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            otherWorkspaceId,
            _session.Id,
            "caseOtherWs"
        );
        Assert.NotNull(wsSpilled);

        var readWs = await Call("read_file", new { file_path = wsSpilled });
        Assert.False(readWs.Success);
        Assert.Contains(PathSafetyValidator.Codes.OutsideWorkspace, readWs.Error ?? string.Empty);
    }

    [Fact(DisplayName = "写链不受只读豁免影响")]
    public async Task WriteChainNeverUsesAllowedRoots()
    {
        string big = BuildBigText("zebra-marker-4403");
        var (_, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "caseWriteChain"
        );
        Assert.NotNull(spilled);
        string before = File.ReadAllText(spilled!);

        var write = await Call("write_file", new { file_path = spilled, content = "tampered\n" });
        Assert.False(write.Success);
        Assert.Contains(PathSafetyValidator.Codes.OutsideWorkspace, write.Error ?? string.Empty);
        Assert.Equal(before, File.ReadAllText(spilled!));

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["file_path"] = spilled! }
        );
        string[] roots = SessionOutputRoots.GetSessionReadAllowedRoots(_context);
        var writeCheck = ToolPipeline.CheckSafety("write_file", json, _projectDir, roots);
        Assert.False(writeCheck.Allowed);
    }

    [Fact(DisplayName = "输出目录内非落盘形态单文件仍拒绝读取")]
    public async Task NonCallFileInOutputDirDenied()
    {
        string dir = SessionOutputRoots.BuildSessionToolOutputsDir(
            _tempRoot,
            _workspace.Id,
            _session.Id
        );
        Directory.CreateDirectory(dir);
        string evil = Path.Combine(dir, "evil.txt");
        File.WriteAllText(evil, "not-a-spill\n", new UTF8Encoding(false));

        var read = await Call("read_file", new { file_path = evil });
        Assert.False(read.Success);
        Assert.Contains(PathSafetyValidator.Codes.OutsideWorkspace, read.Error ?? string.Empty);

        var list = await Call("list_dir", new { file_path = dir });
        Assert.True(list.Success, list.Error);
        Assert.Contains("evil.txt", list.Output);
    }

    [Fact(DisplayName = "落盘目录与存储中枢口径一致")]
    public void SpillDirMatchesStorageManager()
    {
        string ws = "ws-" + Guid.NewGuid().ToString("N");
        string sess = "sess-" + Guid.NewGuid().ToString("N");

        string built = SessionOutputRoots.BuildSessionToolOutputsDir(_tempRoot, ws, sess);
        string managed = _storageManager.GetSessionToolOutputsDir(ws, sess);
        Assert.Equal(Path.GetFullPath(managed), Path.GetFullPath(built));

        string fallback = SessionOutputRoots.BuildSessionToolOutputsDir(null, ws, sess);
        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "sessions", ws, sess, "tool-outputs")
            ),
            Path.GetFullPath(fallback)
        );

        var emptyCtx = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = ws,
            SessionId = sess,
            DataRoot = string.Empty,
        };
        string[] roots = SessionOutputRoots.GetSessionReadAllowedRoots(emptyCtx);
        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(fallback), Path.GetFullPath(roots[0]));
    }

    [Fact(DisplayName = "异常会话标识不产生豁免且不抛异常")]
    public async Task MalformedSessionIdYieldsNoRoots()
    {
        string[] badSessions = { "..", "a/b", "a\\b", string.Empty, "   ", "sess/../x" };
        foreach (string bad in badSessions)
        {
            var ctx = new ToolContext
            {
                WorkspaceRoot = _projectDir,
                WorkspaceId = _workspace.Id,
                SessionId = bad,
                DataRoot = _tempRoot,
            };
            Assert.Empty(SessionOutputRoots.GetSessionReadAllowedRoots(ctx));
        }

        var badWs = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = "../escape",
            SessionId = _session.Id,
            DataRoot = _tempRoot,
        };
        Assert.Empty(SessionOutputRoots.GetSessionReadAllowedRoots(badWs));

        string big = BuildBigText("zebra-marker-3304");
        var (_, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "caseMalformed"
        );
        Assert.NotNull(spilled);

        var traversalCtx = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = "..",
            DataRoot = _tempRoot,
        };
        var denied = await Call("read_file", new { file_path = spilled }, traversalCtx);
        Assert.False(denied.Success);
        Assert.Contains(PathSafetyValidator.Codes.OutsideWorkspace, denied.Error ?? string.Empty);
    }
}
