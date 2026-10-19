#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need15 P2 架构：预览 + 备份撤销 + 自愈闭环 + 统一入口。对应 04 验收矩阵 P2。</summary>
public sealed class Stage15P2ArchitectureTests : IDisposable
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

    public Stage15P2ArchitectureTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"Halo15P2_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"Halo15P2Bs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _registry = new ToolRegistry();
        _projectDir = Path.Combine(_tempRoot, "Projects", "Need15P2");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service.AddOrUpdateWorkspaceAsync(_projectDir, "need15 P2").GetAwaiter().GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "p2").GetAwaiter().GetResult();
        _context = new ToolContext { WorkspaceRoot = _projectDir, WorkspaceId = _workspace.Id, SessionId = _session.Id, DataRoot = _tempRoot };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot)) Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    [Fact(DisplayName = "P2_1a: dry_run 三工具均不改 mtime/bytes，但含 DRY_RUN + @@")]
    public async Task P2_1a_DryRun_NoWrite()
    {
        const string f1 = "dry1.txt";
        File.WriteAllText(Abs(f1), "alpha-dry-line\nbeta-dry-line\n", new UTF8Encoding(false));
        var before1 = File.ReadAllBytes(Abs(f1));
        var t1 = File.GetLastWriteTimeUtc(Abs(f1));
        var r1 = await Call("edit_file", new { file_path = f1, old_string = "alpha-dry-line", new_string = "alpha-DRY-line", dry_run = true });
        Assert.True(r1.Success, r1.Error);
        Assert.Contains("DRY_RUN", r1.Output ?? string.Empty);
        Assert.Contains("@@", r1.Output ?? string.Empty);
        Assert.Equal(before1, File.ReadAllBytes(Abs(f1)));

        var r2 = await Call("edit_lines", new { file_path = f1, offset = 1, limit = 1, new_text = "alpha-DRY2-line", dry_run = true });
        Assert.True(r2.Success, r2.Error);
        Assert.Contains("DRY_RUN", r2.Output ?? string.Empty);
        Assert.Equal(before1, File.ReadAllBytes(Abs(f1)));

        var r3 = await Call("write_file", new { file_path = f1, content = "overwrite-dry-content\n", allow_overwrite = true, dry_run = true });
        Assert.True(r3.Success, r3.Error);
        Assert.Contains("DRY_RUN", r3.Output ?? string.Empty);
        Assert.Equal(before1, File.ReadAllBytes(Abs(f1)));
        Assert.True(File.GetLastWriteTimeUtc(Abs(f1)) == t1);
    }

    [Fact(DisplayName = "P2_1b: dry_run + 错 hash 仍报 EXPECTED_HASH_MISMATCH")]
    public async Task P2_1b_DryRun_Validates()
    {
        const string f = "dryhash.txt";
        File.WriteAllText(Abs(f), "content-line-one\ncontent-line-two\n", new UTF8Encoding(false));
        var r = await Call("edit_lines", new { file_path = f, offset = 1, limit = 1, new_text = "X", expected_hash = new string('1', 64), dry_run = true });
        Assert.False(r.Success);
        Assert.StartsWith("EXPECTED_HASH_MISMATCH", r.Error ?? string.Empty);
    }

    [Fact(DisplayName = "P2_2a: 三次写后 3 bak + 3 meta；undo 恢复到第二次")]
    public async Task P2_2a_BackupChain()
    {
        const string f = "chain.txt";
        File.WriteAllText(Abs(f), "v0-content-line\n", new UTF8Encoding(false));
        var w1 = await Call("write_file", new { file_path = f, content = "v1-content-line\n", allow_overwrite = true });
        Assert.True(w1.Success, w1.Error);
        var e1 = await Call("edit_file", new { file_path = f, old_string = "v1-content-line", new_string = "v2-content-line" });
        Assert.True(e1.Success, e1.Error);
        var e2 = await Call("edit_lines", new { file_path = f, offset = 1, limit = 1, new_text = "v3-content-line" });
        Assert.True(e2.Success, e2.Error);
        string bakDir = Path.Combine(_tempRoot, "backups", _workspace.Id, _session.Id);
        Assert.Equal(3, Directory.GetFiles(bakDir, "*.bak").Length);
        Assert.Equal(3, Directory.GetFiles(bakDir, "*.meta.json").Length);
        string[] baks = Directory.GetFiles(bakDir, "*.bak");
        Array.Sort(baks, StringComparer.Ordinal);
        string latest = baks[^1]; // v2 内容
        var undo = await Call("undo_edit", new { backup_path = latest });
        Assert.True(undo.Success, undo.Error);
        Assert.Contains("v2-content-line", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P2_2b: 55 备份 → prune 到 50")]
    public async Task P2_2b_Prune()
    {
        const string f = "prune.txt";
        File.WriteAllText(Abs(f), "prune-v0-line\n", new UTF8Encoding(false));
        for (int i = 1; i <= 55; i++)
        {
            var r = await Call("write_file", new { file_path = f, content = $"prune-v{i}-line-content\n", allow_overwrite = true });
            Assert.True(r.Success, r.Error);
        }

        string bakDir = Path.Combine(_tempRoot, "backups", _workspace.Id, _session.Id);
        Assert.Equal(50, Directory.GetFiles(bakDir, "*.bak").Length);
    }

    [Fact(DisplayName = "P2_2c: backup 越权路径拒绝")]
    public async Task P2_2c_PathEscape_Rejected()
    {
        var r = await Call("undo_edit", new { backup_path = "C:/Windows/win.ini" });
        Assert.False(r.Success);
    }

    [Fact(DisplayName = "P2_3: 三类码回包自动带 NextAction 草稿")]
    public void P2_3_NextAction_Emitted()
    {
        var notFound = ToolResult.FailCode("OLD_STRING_NOT_FOUND", "OLD_STRING_NOT_FOUND：{\"ok\":false,\"code\":\"OLD_STRING_NOT_FOUND\",\"file\":\"a.txt\",\"old_string_lines\":2,\"nearest\":[{\"line\":10,\"score\":0.9,\"preview\":\"x\"}]}", "{\"ok\":false,\"code\":\"OLD_STRING_NOT_FOUND\",\"file\":\"a.txt\",\"old_string_lines\":2,\"nearest\":[{\"line\":10,\"score\":0.9,\"preview\":\"x\"}]}");
        string h1 = AgentOrchestrator.BuildNextActionHint(notFound, "edit_file", "{\"file_path\":\"a.txt\"}", "OLD_STRING_NOT_FOUND");
        Assert.Contains("NextAction", h1);
        using var doc = JsonDocument.Parse(h1.Substring(h1.IndexOf("edit_lines(") + "edit_lines(".Length).TrimEnd(')'));
        Assert.Equal(10, doc.RootElement.GetProperty("offset").GetInt32());

        var needConfirm = ToolResult.FailCode("NEED_CONFIRM_OVERWRITE", "NEED_CONFIRM_OVERWRITE：目标已存在", null);
        string h2 = AgentOrchestrator.BuildNextActionHint(needConfirm, "write_file", "{}", "NEED_CONFIRM_OVERWRITE");
        Assert.Contains("NextAction", h2);
        Assert.Contains("allow_overwrite", h2);

        var missing = ToolResult.FailCode("MISSING_LIMIT", "MISSING_LIMIT：缺少 limit", null);
        string h3 = AgentOrchestrator.BuildNextActionHint(missing, "edit_lines", "{}", "MISSING_LIMIT");
        Assert.Contains("NextAction", h3);
        Assert.Contains("limit", h3);

        // HintForTool 每个码非空
        Assert.False(string.IsNullOrWhiteSpace(AgentOrchestrator.HintForTool("edit_file", "OLD_STRING_NOT_FOUND")));
        Assert.False(string.IsNullOrWhiteSpace(AgentOrchestrator.HintForTool("edit_file", "ANCHOR_AMBIGUOUS")));
        Assert.False(string.IsNullOrWhiteSpace(AgentOrchestrator.HintForTool("write_file", "NEED_CONFIRM_OVERWRITE")));
        Assert.False(string.IsNullOrWhiteSpace(AgentOrchestrator.HintForTool("edit_lines", "MISSING_LIMIT")));
    }

    [Fact(DisplayName = "P2_4a: 统一 edit(mode=text) 分发成功")]
    public async Task P2_4a_UnifiedText()
    {
        const string f = "unified.txt";
        File.WriteAllText(Abs(f), "aaa-unified-line\nbbb-unified-line\n", new UTF8Encoding(false));
        var r = await Call("edit", new { file_path = f, mode = "text", old_string = "aaa-unified-line", new_string = "aaa-UNIFIED-line" });
        Assert.True(r.Success, r.Error);
        Assert.Contains("aaa-UNIFIED-line", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P2_4b: 统一 edit(mode=lines) 分发成功")]
    public async Task P2_4b_UnifiedLines()
    {
        const string f = "unified2.txt";
        File.WriteAllText(Abs(f), "one-unified-line\ntwo-unified-line\n", new UTF8Encoding(false));
        var r = await Call("edit", new { file_path = f, mode = "lines", offset = 2, limit = 1, new_text = "TWO-unified-line" });
        Assert.True(r.Success, r.Error);
        Assert.Contains("TWO-unified-line", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    private string Abs(string n) => Path.Combine(_projectDir, n);
    private Task<ToolResult> Call(string name, object args) =>
        _registry.DispatchAsync(new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), name, Args(args)), _context);
    private static string Args(object o) =>
        JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
