#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need22-01b §1 前置 + 01c 验收 A1–A6：P0 攻击样本库（5+3 条）。</summary>
public sealed class Stage15P0AttackSamples : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly string _projectDir;
    private readonly string _outsideDir;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly ToolRegistry _registry;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;
    private readonly ToolContext _context;

    public Stage15P0AttackSamples()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloP0Atk_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloP0AtkBs_{Guid.NewGuid():N}");
        _outsideDir = Path.Combine(Path.GetTempPath(), $"HaloP0Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        Directory.CreateDirectory(_outsideDir);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _registry = new ToolRegistry();
        _projectDir = Path.Combine(_tempRoot, "Projects", "Need22P0");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "need22 P0 attack")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "atk").GetAwaiter().GetResult();
        _context = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
            DataRoot = _tempRoot
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
            if (Directory.Exists(_outsideDir))
                Directory.Delete(_outsideDir, recursive: true);
        }
        catch { }
    }

    // ---------------- A1 路径逃逸 ----------------

    [Fact(DisplayName = "A1-1: ../ 写逃逸拒绝 PATH_OUTSIDE_WORKSPACE 且无文件写出")]
    public async Task A1_Write_Traversal_Rejected()
    {
        string evilOutside = Path.Combine(_outsideDir, "evil_a1.txt");
        var r = await Call(
            "write_file",
            new { file_path = "../../evil_a1.txt", content = "pwned\n" }
        );
        Assert.False(r.Success);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", r.Error ?? string.Empty);
        Assert.False(File.Exists(evilOutside));
        // 工作区内也不应出现同名文件（解析后目标在区外）
        Assert.False(File.Exists(Path.Combine(_projectDir, "evil_a1.txt")));
    }

    [Fact(DisplayName = "A1-2: 绝对路径读区外文件拒绝")]
    public async Task A1_Read_AbsoluteOutside_Rejected()
    {
        string secret = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(secret, "top-secret\n", new UTF8Encoding(false));
        var r = await Call("read_file", new { file_path = secret });
        Assert.False(r.Success);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", r.Error ?? string.Empty);
    }

    [Fact(DisplayName = "A1-3: list_dir / grep_search 越界拒绝")]
    public async Task A1_ListGrep_Outside_Rejected()
    {
        var l = await Call("list_dir", new { path = _outsideDir });
        Assert.False(l.Success);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", l.Error ?? string.Empty);
        var g = await Call("grep_search", new { pattern = "secret", path = _outsideDir });
        Assert.False(g.Success);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", g.Error ?? string.Empty);
    }

    [Fact(DisplayName = "A1-4: 区内 symlink→区外写穿越拒绝")]
    public void A1_Symlink_Traversal_Rejected()
    {
        string linkDir = Path.Combine(_projectDir, "link_out");
        try
        {
            Directory.CreateSymbolicLink(linkDir, _outsideDir);
        }
        catch (Exception)
        {
            // 无特权创建软链时：至少证明解析器对区外真实路径可用，降级通道不断
            string probe = Path.Combine(_outsideDir, "probe.txt");
            File.WriteAllText(probe, "x\n");
            Assert.NotNull(PathSafetyValidator.TryResolveFinalPath(probe));
            return;
        }
        string viaLink = Path.Combine(linkDir, "evil_link.txt");
        var (ok, err, _, outside) = PathSafetyValidator.ValidateWrite(_projectDir, viaLink);
        Assert.False(ok);
        Assert.True(outside);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", err ?? string.Empty);
        Assert.False(File.Exists(Path.Combine(_outsideDir, "evil_link.txt")));
    }

    [Fact(DisplayName = "A1-5: 工作区内正常读写不受误伤")]
    public async Task A1_InsideWorkspace_Ok()
    {
        var w = await Call("write_file", new { file_path = "ok/inner.txt", content = "hello\n" });
        Assert.True(w.Success, w.Error);
        var r = await Call("read_file", new { file_path = "ok/inner.txt" });
        Assert.True(r.Success, r.Error);
        Assert.Contains("hello", r.Output);
    }

    // ---------------- A2 命令约束 ----------------

    [Fact(DisplayName = "A2-1: 越界 workdir 拒绝")]
    public async Task A2_WorkdirOutside_Rejected()
    {
        var r = await CallRaw(
            "exec_command",
            "{\"command\":\"echo hi\",\"workdir\":\"" + _outsideDir.Replace("\\", "\\\\") + "\"}"
        );
        Assert.False(r.Success);
        Assert.Contains("PATH_OUTSIDE_WORKSPACE", r.Error ?? string.Empty);
    }

    [Fact(DisplayName = "A2-2: 超长 command 拒绝 COMMAND_TOO_LONG")]
    public async Task A2_TooLong_Rejected()
    {
        string big = "echo " + new string('x', 8192);
        var r = await Call("exec_command", new { command = big });
        Assert.False(r.Success);
        Assert.Equal("COMMAND_TOO_LONG", r.ErrorCode);
    }

    [Fact(DisplayName = "A2-3: timeout<=0 非法 INVALID_TIMEOUT")]
    public async Task A2_ZeroTimeout_Rejected()
    {
        var r0 = await Call("exec_command", new { command = "echo hi", timeout_ms = 0 });
        Assert.False(r0.Success);
        Assert.Equal("INVALID_TIMEOUT", r0.ErrorCode);
        var rn = await Call("exec_command", new { command = "echo hi", timeout_ms = -5 });
        Assert.False(rn.Success);
        Assert.Equal("INVALID_TIMEOUT", rn.ErrorCode);
    }

    [Fact(DisplayName = "A2-4: 白名单外命令拒绝 COMMAND_NOT_ALLOWED")]
    public async Task A2_Allowlist_Rejected()
    {
        var r = await Call("exec_command", new { command = "powershell Get-Process" });
        Assert.False(r.Success);
        Assert.Equal("COMMAND_NOT_ALLOWED", r.ErrorCode);
    }

    [Fact(DisplayName = "A2-5: 白名单内 echo 放行（Windows）")]
    public async Task A2_Allowlist_Echo_Ok()
    {
        if (
            !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows
            )
        )
        {
            return;
        }
        var r = await Call("exec_command", new { command = "echo p0-ok" });
        Assert.True(r.Success, r.Error);
        Assert.Contains("p0-ok", r.Output);
    }

    // ---------------- A3 未知工具 ----------------

    [Fact(DisplayName = "A3: 未知工具 UNKNOWN_TOOL（Validator + Dispatch 双层）")]
    public async Task A3_UnknownTool_Rejected()
    {
        var v = ToolCallValidator.Validate("nope_tool_xyz", "{}");
        Assert.False(v.Success);
        Assert.Equal("UNKNOWN_TOOL", v.Code);
        var d = await _registry.DispatchAsync(
            new ToolCallDto("c1", "nope_tool_xyz", "{}"),
            _context
        );
        Assert.False(d.Success);
        Assert.Equal("UNKNOWN_TOOL", d.ErrorCode);
    }

    // ---------------- A4 Schema 对齐 ----------------

    [Fact(DisplayName = "A4-1: skill_tool create 走通校验")]
    public void A4_Skill_Create_Valid()
    {
        var r = ToolCallValidator.Validate(
            "skill_tool",
            "{\"action\":\"create\",\"name\":\"ab\",\"content\":\"# T\\nbody\"}"
        );
        Assert.True(r.Success, r.Message);
    }

    [Fact(DisplayName = "A4-2: goal_tool progress 走通校验")]
    public void A4_Goal_Progress_Valid()
    {
        var r = ToolCallValidator.Validate("goal_tool", "{\"action\":\"progress\",\"percent\":50}");
        Assert.True(r.Success, r.Message);
    }

    // ---------------- A5 备份可靠 ----------------

    [Fact(DisplayName = "A5-1: 并发 20 写备份一致")]
    public async Task A5_Concurrent_Backup_Consistent()
    {
        string f = Path.Combine(_projectDir, "conc.txt");
        File.WriteAllText(f, "concurrent-base-line\n", new UTF8Encoding(false));
        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ =>
                Task.Run(
                    () => BackupStore.WriteBackup(f, _tempRoot, _workspace.Id, _session.Id, "atk")
                )
            );
        var results = await Task.WhenAll(tasks);
        byte[] expect = File.ReadAllBytes(f);
        foreach (var r in results)
        {
            Assert.NotNull(r.BakPath);
            Assert.Equal(expect, File.ReadAllBytes(r.BakPath!));
        }
    }

    [Fact(DisplayName = "A5-2: 配额超限拒绝 BACKUP_QUOTA_EXCEEDED")]
    public void A5_Quota_Rejected()
    {
        long saved = BackupStore.GlobalQuotaBytes;
        try
        {
            BackupStore.GlobalQuotaBytes = 10; // 极小配额必超
            string f = Path.Combine(_projectDir, "quota.txt");
            File.WriteAllText(
                f,
                "quota-base-line-longer-than-ten-bytes\n",
                new UTF8Encoding(false)
            );
            var ex = Assert.Throws<InvalidOperationException>(
                () => BackupStore.WriteBackup(f, _tempRoot, _workspace.Id, _session.Id, "atk")
            );
            Assert.Contains("BACKUP_QUOTA_EXCEEDED", ex.Message);
        }
        finally
        {
            BackupStore.GlobalQuotaBytes = saved;
        }
    }

    [Fact(DisplayName = "A5-3: Undo 精确找回（meta 缺失走 index）")]
    public async Task A5_Undo_Roundtrip()
    {
        const string f = "undo_me.txt";
        const string orig = "original-undo-line\n";
        var w = await Call("write_file", new { file_path = f, content = orig });
        Assert.True(w.Success, w.Error);
        var e = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "original-undo-line\n",
                new_string = "tampered-undo-line\n",
                match_mode = "exact",
            }
        );
        Assert.True(e.Success, e.Error);
        string dir = BackupStore.SessionBackupDir(_tempRoot, _workspace.Id, _session.Id);
        string bak = Directory
            .GetFiles(dir, "*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        // 删除 meta，强制走 index.json 精确回查
        string meta = bak + ".meta.json";
        if (File.Exists(meta))
            File.Delete(meta);
        var u = await Call("undo_edit", new { backup_path = bak });
        Assert.True(u.Success, u.Error);
        Assert.Equal(orig, File.ReadAllText(Path.Combine(_projectDir, f), Encoding.UTF8));
    }

    // ---------------- A6 敏感封堵 ----------------

    [Fact(DisplayName = "A6-1: 非法 callId 拒绝落盘 INVALID_CALL_ID")]
    public void A6_BadCallId_NoSpill()
    {
        string big = new string('y', 17000);
        var (display, spilled) = ToolOutputGuard.ApplyGuard(big, _tempRoot, "w", "s", "../../x");
        Assert.Null(spilled);
        Assert.Contains("INVALID_CALL_ID", display);
        Assert.False(
            Directory.Exists(Path.Combine(_tempRoot, "sessions", "w", "s", "tool-outputs", "x"))
        );
    }

    [Fact(DisplayName = "A6-2: spill 落盘前脱敏无 sk- 明文")]
    public void A6_Spill_Scrubbed()
    {
        string secret = "sk-proj-abcdef1234567890SECRET";
        string big = "prefix " + secret + " " + new string('z', 17000);
        var (display, spilled) = ToolOutputGuard.ApplyGuard(
            big,
            _tempRoot,
            _workspace.Id,
            _session.Id,
            "call_a6secret"
        );
        Assert.NotNull(spilled);
        string onDisk = File.ReadAllText(spilled!);
        Assert.DoesNotContain(secret, onDisk);
        Assert.Contains("[REDACTED_SECRET]", onDisk);
        Assert.DoesNotContain(secret, display);
    }

    [Fact(DisplayName = "A6-3: MaskSecret 全掩码不泄露首尾")]
    public void A6_Mask_Full()
    {
        string m = Host.Desktop.Infrastructure.Storage.SecretProtector.MaskSecret(
            "sk-proj-abcdef-9876543210"
        );
        Assert.DoesNotContain("9876543210", m);
        Assert.DoesNotContain("abcdef", m);
        Assert.Contains("****", m);
    }

    [Fact(DisplayName = "A6-4: 错误回显无绝对路径")]
    public async Task A6_Error_NoAbsPath()
    {
        var r = await Call("read_file", new { file_path = "../../nope.txt" });
        Assert.False(r.Success);
        Assert.DoesNotContain(_projectDir, r.Error ?? string.Empty);
        Assert.DoesNotContain(":\\", r.Error ?? string.Empty);
    }

    private string Abs(string n) => Path.Combine(_projectDir, n);

    private Task<ToolResult> Call(string name, object args) =>
        _registry.DispatchAsync(
            new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), name, Args(args)),
            _context
        );

    private Task<ToolResult> CallRaw(string name, string json) =>
        _registry.DispatchAsync(
            new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), name, json),
            _context
        );

    private static string Args(object o) =>
        JsonSerializer.Serialize(
            o,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
}
