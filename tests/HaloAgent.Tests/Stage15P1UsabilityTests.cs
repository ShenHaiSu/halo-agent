#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need15 P1 加固：提示词 + Schema + 回吐。对应 04 验收矩阵 P1。</summary>
public sealed class Stage15P1UsabilityTests : IDisposable
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

    public Stage15P1UsabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"Halo15P1_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"Halo15P1Bs_{Guid.NewGuid():N}");
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "Need15P1");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "need15 P1")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "p1").GetAwaiter().GetResult();
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

    [Fact(DisplayName = "P1_1: BuildAsync 含三硬规则串 (need15)")]
    public async Task P1_1_Guide_ContainsHardRules()
    {
        string text = await SystemPromptBuilder.BuildAsync(_workspace);
        Assert.Contains("NEVER invent", text);
        Assert.Contains("verbatim copy-paste", text);
        Assert.Contains("allow_overwrite:true", text);
    }

    [Fact(DisplayName = "P1_2a: Schema 无 expand_tabs 但老参数仍解析")]
    public async Task P1_2a_Schema_HidesAdvancedKnobs()
    {
        var defs = _registry.GetDefinitions();
        var edit = defs.First(d => d.Name == "edit_file");
        Assert.DoesNotContain("expand_tabs", edit.ParametersJson);
        // 老参数仍解析：传 expand_tabs:0 不应炸，功能等价
        const string f = "knob.cs";
        File.WriteAllText(Abs(f), "\tint a = 1;\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "    int a = 1;",
                new_string = "    int a = 2;",
                expand_tabs = 0
            }
        );
        // expand_tabs=0 关闭展开，空格锚点对 Tab 落盘应未命中（证明参数被读取）
        Assert.False(r.Success);
        Assert.Contains("OLD_STRING_NOT_FOUND", r.Error ?? string.Empty);
        // 双协议合法 JSON
        using var chat = JsonDocument.Parse(_registry.GetChatToolsJson());
        using var resp = JsonDocument.Parse(_registry.GetResponsesToolsJson());
        Assert.True(chat.RootElement.GetArrayLength() >= 12);
        Assert.True(resp.RootElement.GetArrayLength() >= 12);
    }

    [Fact(DisplayName = "P1_2b: old_string:x → ANCHOR_TOO_SHORT")]
    public async Task P1_2b_ShortAnchor_RejectedFast()
    {
        const string f = "short.txt";
        File.WriteAllText(Abs(f), "abcdefghij-line-content\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "x",
                new_string = "y"
            }
        );
        Assert.False(r.Success);
        Assert.StartsWith("ANCHOR_TOO_SHORT", r.Error ?? string.Empty);
        Assert.Equal("abcdefghij-line-content\n", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P1_3a: edit_file 成功含 64hex hash 且与落盘一致")]
    public async Task P1_3a_EditFile_ReturnsHash()
    {
        const string f = "hashcheck.txt";
        File.WriteAllText(
            Abs(f),
            "alpha-line-content\nbeta-line-content\n",
            new UTF8Encoding(false)
        );
        var r = await Call(
            "edit_file",
            new
            {
                file_path = f,
                old_string = "alpha-line-content",
                new_string = "alpha-LINE-content"
            }
        );
        Assert.True(r.Success, r.Error);
        var m = Regex.Match(r.Output ?? string.Empty, @"hash: ([0-9a-f]{64})");
        Assert.True(m.Success, "输出缺 64hex hash: " + r.Output);
        string diskHash = Sha256Hex(File.ReadAllBytes(Abs(f)));
        Assert.Equal(diskHash, m.Groups[1].Value);
    }

    [Fact(DisplayName = "P1_3b: read hash → edit_lines(expected_hash) 联动通过")]
    public async Task P1_3b_ReadHash_Chain()
    {
        const string f = "chain.txt";
        File.WriteAllText(
            Abs(f),
            "one-line-content\ntwo-line-content\nthree-line-content\n",
            new UTF8Encoding(false)
        );
        var read = await Call("read_file", new { file_path = f });
        Assert.True(read.Success, read.Error);
        var m = Regex.Match(read.Output ?? string.Empty, @"Hash: ([0-9a-f]{64})");
        Assert.True(m.Success, "read_file 缺 Hash: " + read.Output);
        var edit = await Call(
            "edit_lines",
            new
            {
                file_path = f,
                offset = 2,
                limit = 1,
                new_text = "TWO-line-content",
                expected_hash = m.Groups[1].Value
            }
        );
        Assert.True(edit.Success, edit.Error);
    }

    [Fact(DisplayName = "P1_3c: 三工具成功 FileBytes>0")]
    public async Task P1_3c_FileBytes_Filled()
    {
        const string f1 = "fb1.txt";
        File.WriteAllText(Abs(f1), "aaa-line-content\nbbb-line-content\n", new UTF8Encoding(false));
        var e1 = await Call(
            "edit_file",
            new
            {
                file_path = f1,
                old_string = "aaa-line-content",
                new_string = "aaa-LINE-content"
            }
        );
        Assert.True(e1.Success, e1.Error);
        Assert.True(e1.FileBytes > 0);
        var e2 = await Call(
            "edit_lines",
            new
            {
                file_path = f1,
                offset = 1,
                limit = 1,
                new_text = "aaa-LINE2-content"
            }
        );
        Assert.True(e2.Success, e2.Error);
        Assert.True(e2.FileBytes > 0);
        var w = await Call(
            "write_file",
            new
            {
                file_path = f1,
                content = "brand-new-content-line\n",
                allow_overwrite = true
            }
        );
        Assert.True(w.Success, w.Error);
        Assert.True(w.FileBytes > 0);
    }

    [Fact(DisplayName = "P1_4a: 空文件 offset=1,limit=0 插入成功")]
    public async Task P1_4a_EmptyFile_Insert()
    {
        const string f = "empty.txt";
        File.WriteAllText(Abs(f), string.Empty, new UTF8Encoding(false));
        var r = await Call(
            "edit_lines",
            new
            {
                file_path = f,
                offset = 1,
                limit = 0,
                new_text = "first-line-content"
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("first-line-content", File.ReadAllText(Abs(f), Encoding.UTF8));
    }

    [Fact(DisplayName = "P1_4b: 孤立 \\r 越界文案含双口径行数")]
    public async Task P1_4b_LoneCR_OutOfRange_DualCaliber()
    {
        const string f = "lonecr.txt";
        // read_file 口径 Split('\n') = 1 行；edit_lines 物理行 = 3 行
        File.WriteAllBytes(Abs(f), Encoding.UTF8.GetBytes("aaa\rbbb\rccc"));
        var r = await Call(
            "edit_lines",
            new
            {
                file_path = f,
                offset = 5,
                limit = 1,
                new_text = "X"
            }
        );
        Assert.False(r.Success);
        Assert.StartsWith("OUT_OF_RANGE", r.Error ?? string.Empty);
        Assert.Contains("read_file", r.Error ?? string.Empty);
    }

    private string Abs(string n) => Path.Combine(_projectDir, n);

    private Task<ToolResult> Call(string name, object args) =>
        _registry.DispatchAsync(
            new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), name, Args(args)),
            _context
        );

    private static string Args(object o) =>
        JsonSerializer.Serialize(
            o,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

    private static string Sha256Hex(byte[] b)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(b)).ToLowerInvariant();
    }
}
