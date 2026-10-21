#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need15 P0 止血：5 个必修安全修复。对应 04 验收矩阵 P0。</summary>
public sealed class Stage15P0SafetyTests : IDisposable
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

    public Stage15P0SafetyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"Halo15P0_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"Halo15P0Bs_{Guid.NewGuid():N}");
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "Need15P0");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "need15 P0")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "p0").GetAwaiter().GetResult();
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
        }
        catch { }
    }

    [Fact(DisplayName = "P0_1a: regex 含 $ 按字面写回")]
    public async Task P0_1a_RegexDollar_IsLiteral()
    {
        const string f = "dollar.cs";
        File.WriteAllText(
            Abs(f),
            "price = \"$5\";\nvar s = $\"v={100}\";\n// anchor-target-line\n",
            new UTF8Encoding(false)
        );
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "// anchor-target-line",
                new_string = "price = \"$9\"; // $PATH ${HOME} $$",
                match_mode = "regex"
            }
        );
        Assert.True(r.Success, r.Error);
        string after = File.ReadAllText(Abs(f), Encoding.UTF8);
        Assert.Contains("price = \"$9\"; // $PATH ${HOME} $$", after);
        Assert.Contains("$5", after);
    }

    [Fact(DisplayName = "P0_1b: regex_expand_groups:true 时 $1 生效")]
    public async Task P0_1b_RegexExpand_OptIn()
    {
        const string f = "expand.cs";
        File.WriteAllText(Abs(f), "aaa-111-bbb\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "(aaa)-(111)",
                new_string = "$2$1",
                match_mode = "regex",
                regex_expand_groups = true
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("111aaa", File.ReadAllText(Abs(f), Encoding.UTF8));
        // 默认字面：同样模式不展开
        File.WriteAllText(Abs(f), "aaa-111-bbb\n", new UTF8Encoding(false));
        var r2 = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "(aaa)-(111)",
                new_string = "$2$1",
                match_mode = "regex"
            }
        );
        Assert.True(r2.Success, r2.Error);
        Assert.Contains("$2$1", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P0_2a: 覆盖缺 flag → NEED_CONFIRM_OVERWRITE 且字节不变")]
    public async Task P0_2a_Overwrite_RequiresFlag()
    {
        const string f = "overwrite.txt";
        File.WriteAllText(Abs(f), "original-content-line\n", new UTF8Encoding(false));
        string before = File.ReadAllText(Abs(f), Encoding.UTF8);
        var r = await Call("write_file", new { file_path = f, content = "hacked\n" });
        Assert.False(r.Success);
        Assert.StartsWith("NEED_CONFIRM_OVERWRITE", r.Error ?? string.Empty);
        Assert.Equal("NEED_CONFIRM_OVERWRITE", r.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P0_2b: CONTENT_TOO_LARGE（300KB）且文件不变")]
    public async Task P0_2b_TooLarge_Rejected()
    {
        const string f = "big.txt";
        File.WriteAllText(Abs(f), "small\n", new UTF8Encoding(false));
        string big = new string('x', 300 * 1024);
        var r = await Call(
            "write_file",
            new
            {
                file_path = f,
                content = big,
                allow_overwrite = true
            }
        );
        Assert.False(r.Success);
        Assert.StartsWith("CONTENT_TOO_LARGE", r.Error ?? string.Empty);
        Assert.Equal("small\n", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P0_2c: hash 错拒绝、对则放行 + .bak 与旧字节一致")]
    public async Task P0_2c_Hash_Guards()
    {
        const string f = "hashguard.txt";
        File.WriteAllText(Abs(f), "version-one-line-content\n", new UTF8Encoding(false));
        byte[] oldBytes = File.ReadAllBytes(Abs(f));
        var bad = await Call(
            "write_file",
            new
            {
                file_path = f,
                content = "new-content-line-here\n",
                allow_overwrite = true,
                expected_hash = new string('0', 64)
            }
        );
        Assert.False(bad.Success);
        Assert.StartsWith("EXPECTED_HASH_MISMATCH", bad.Error ?? string.Empty);
        Assert.Equal(oldBytes, File.ReadAllBytes(Abs(f)));
        string goodHash = Sha256Hex(oldBytes);
        var ok = await Call(
            "write_file",
            new
            {
                file_path = f,
                content = "new-content-line-here\n",
                allow_overwrite = true,
                expected_hash = goodHash
            }
        );
        Assert.True(ok.Success, ok.Error);
        Assert.True(ok.FileBytes > 0);
        string? bakDir = FindBackupDir();
        Assert.NotNull(bakDir);
        string[] baks = Directory.GetFiles(bakDir!, "*.bak");
        Assert.NotEmpty(baks);
        // 最新备份字节等于旧字节（至少有一个备份等于旧字节）
        Assert.Contains(baks, b => File.ReadAllBytes(b).SequenceEqual(oldBytes));
    }

    [Fact(DisplayName = "P0_2d: 新建文件无 flag 一次成功")]
    public async Task P0_2d_NewFile_NoFlag_Ok()
    {
        var r = await Call(
            "write_file",
            new { file_path = "sub/new15.txt", content = "hello-need15\n" }
        );
        Assert.True(r.Success, r.Error);
        Assert.True(File.Exists(Abs("sub/new15.txt")));
    }

    [Fact(DisplayName = "P0_3a: 缺 limit → MISSING_LIMIT 且文件不变")]
    public async Task P0_3a_MissingLimit_Rejected()
    {
        const string f = "nolimit.txt";
        const string orig = "a1-line-content\nb2-line-content\n";
        File.WriteAllText(Abs(f), orig, new UTF8Encoding(false));
        var r = await CallRaw(
            "edit_lines",
            "{\"file_path\":\"" + f + "\",\"offset\":1,\"new_text\":\"X\"}"
        );
        Assert.False(r.Success);
        Assert.StartsWith("MISSING_LIMIT", r.Error ?? string.Empty);
        Assert.Equal("MISSING_LIMIT", r.ErrorCode);
        Assert.Equal(orig, File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P0_3b: limit:0 插入行为不变")]
    public async Task P0_3b_InsertZero_Ok()
    {
        const string f = "insert0.txt";
        File.WriteAllText(Abs(f), "line-one-content\nline-two-content\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_lines",
            new
            {
                file_path = f,
                offset = 1,
                limit = 0,
                new_text = "inserted-marker-line"
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.StartsWith(
            "inserted-marker-line",
            File.ReadAllText(Abs(f), Encoding.UTF8).Replace("\r\n", "\n")
        );
    }

    [Fact(DisplayName = "P0_4a: 5 处同锚点 → ANCHOR_AMBIGUOUS 含行号")]
    public async Task P0_4a_Ambiguous_Rejected_WithLines()
    {
        const string f = "ambig.cs";
        var sb = new StringBuilder();
        sb.Append("class Wrapper {\n");
        for (int i = 0; i < 5; i++)
            sb.Append("  void M" + i + "() {\n    return true;\n  }\n");
        sb.Append("}\n");
        File.WriteAllText(Abs(f), sb.ToString(), new UTF8Encoding(false));
        string before = File.ReadAllText(Abs(f), Encoding.UTF8);
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "    return true;",
                new_string = "    return false;"
            }
        );
        Assert.False(r.Success);
        Assert.StartsWith("ANCHOR_AMBIGUOUS", r.Error ?? string.Empty);
        Assert.Equal("ANCHOR_AMBIGUOUS", r.ErrorCode);
        Assert.Contains("候选行", r.Error ?? string.Empty);
        Assert.NotNull(r.ErrorPayloadJson);
        Assert.Equal(before, File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P0_4b: 25 处 + replace_all → 成功 + WARN_BULK_EDITS")]
    public async Task P0_4b_BulkWarn()
    {
        const string f = "bulk.txt";
        var sb = new StringBuilder();
        for (int i = 0; i < 25; i++)
            sb.Append("prefix-keep-" + i.ToString("D2") + "-suffix-line\n");
        File.WriteAllText(Abs(f), sb.ToString(), new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "prefix-keep-",
                new_string = "prefix-KEEP-",
                replace_all = true
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("WARN_BULK_EDITS", r.Output ?? string.Empty);
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

    private static string Sha256Hex(byte[] b)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(b)).ToLowerInvariant();
    }

    private string? FindBackupDir()
    {
        string dir = Path.Combine(_tempRoot, "backups", _workspace.Id, _session.Id);
        return Directory.Exists(dir) ? dir : null;
    }
}
