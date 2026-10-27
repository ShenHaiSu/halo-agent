using System.Text.Json;
 using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

/// <summary>
/// 内置工具行为测试：直接调 ToolRegistry / 各工具 ExecuteAsync，用 FakeHost 实现 IToolHost。
/// 错误一律是“文本回给模型”而非抛异常；本文件同时断言这一点。
/// </summary>
public sealed class ToolsTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeHost _host;

    public ToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "halo-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _host = new FakeHost(Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    // ---------- 桩宿主 ----------

    private sealed class FakeHost : IToolHost
    {
        private readonly string _backupDir;

        public FakeHost(string dataDir)
        {
            DataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            _backupDir = Path.Combine(dataDir, "backups");
            Directory.CreateDirectory(_backupDir);
        }

        public string DataDir { get; }
        public string? ProxyUrl => null;
        public List<string> Outputs { get; } = new();
        public List<int> Alerts { get; } = new();
        public List<string> BackupCopies { get; } = new();

        public Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct)
        {
            if (!File.Exists(path))
                return Task.FromResult(new UndoSnapshotResult(false, null, null));
            var dst = Path.Combine(_backupDir, Guid.NewGuid().ToString("N") + ".bak");
            File.Copy(path, dst, overwrite: true);
            BackupCopies.Add(dst);
            return Task.FromResult(new UndoSnapshotResult(true, dst, null));
        }

        public void PublishToolOutput(string toolCallId, string delta) => Outputs.Add(delta);
        public void PublishSilentAlert(string toolCallId, int level) => Alerts.Add(level);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private ToolContext Ctx() => new()
    {
        Cwd = _dir,
        SessionId = "test-session",
        Host = _host,
        Ct = CancellationToken.None,
    };

    private static ITool Tool(ToolRegistry reg, string name) =>
        reg.Get(name) ?? throw new InvalidOperationException("工具缺失: " + name);

    // ---------- 注册表 ----------

    [Fact]
    public void Registry_has_ten_builtin_tools()
    {
        var reg = ToolRegistry.CreateBuiltin();
        Assert.Equal(10, reg.All.Count);
        Assert.Equal(reg.All.Count, reg.Names.Distinct().Count());
        foreach (var n in new[] { "read_file", "write_file", "edit", "list_directory", "glob", "grep_search", "run_shell_command", "memory_write", "memory_read", "web_fetch" })
            Assert.True(reg.Has(n), "缺工具: " + n);
    }

    // ---------- read_file ----------

    [Fact]
    public async Task ReadFile_reads_with_offset_and_limit()
    {
        var f = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(f, "1\n2\n3\n4\n5\n");
        var reg = ToolRegistry.CreateBuiltin();

        var r = await Tool(reg, "read_file").ExecuteAsync(
            Args("{\"path\":\"" + f.Replace("\\", "\\\\") + "\",\"offset\":1,\"limit\":2}"), Ctx(), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Contains("共 5 行", r.Output);
        Assert.Contains("| 2", r.Output);
        Assert.Contains("| 3", r.Output);
        Assert.DoesNotContain("| 4", r.Output);
    }

    [Fact]
    public async Task ReadFile_missing_returns_error_text_not_exception()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "read_file").ExecuteAsync(
            Args("{\"path\":\"" + Path.Combine(_dir, "nope.txt").Replace("\\", "\\\\") + "\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("不存在", r.Output);
    }

    // ---------- write_file ----------

    [Fact]
    public async Task WriteFile_creates_new_file()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var target = Path.Combine(_dir, "sub", "new.txt");
        var r = await Tool(reg, "write_file").ExecuteAsync(
            Args("{\"path\":\"" + target.Replace("\\", "\\\\") + "\",\"content\":\"hello\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("hello", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteFile_overwrite_keeps_undo_backup()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var target = Path.Combine(_dir, "w.txt");
        await File.WriteAllTextAsync(target, "old-content");
        var r = await Tool(reg, "write_file").ExecuteAsync(
            Args("{\"path\":\"" + target.Replace("\\", "\\\\") + "\",\"content\":\"new-content\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("new-content", await File.ReadAllTextAsync(target));
        Assert.Single(_host.BackupCopies);
        Assert.Equal("old-content", await File.ReadAllTextAsync(_host.BackupCopies[0]));
    }

    // ---------- edit ----------

    [Fact]
    public async Task Edit_replaces_unique_match()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var target = Path.Combine(_dir, "e.txt");
        await File.WriteAllTextAsync(target, "hello foo world");
        var r = await Tool(reg, "edit").ExecuteAsync(
            Args("{\"path\":\"" + target.Replace("\\", "\\\\") + "\",\"old_string\":\"foo\",\"new_string\":\"bar\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("hello bar world", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Edit_zero_match_returns_error()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var target = Path.Combine(_dir, "e0.txt");
        await File.WriteAllTextAsync(target, "hello world");
        var r = await Tool(reg, "edit").ExecuteAsync(
            Args("{\"path\":\"" + target.Replace("\\", "\\\\") + "\",\"old_string\":\"zzz\",\"new_string\":\"bar\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("未找到", r.Output);
    }

    [Fact]
    public async Task Edit_multiple_matches_returns_error()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var target = Path.Combine(_dir, "em.txt");
        await File.WriteAllTextAsync(target, "aaa");
         var r = await Tool(reg, "edit").ExecuteAsync(
             Args("{\"path\":\"" + target.Replace("\\", "\\\\") + "\",\"old_string\":\"a\",\"new_string\":\"b\"}"), Ctx(), CancellationToken.None);
         Assert.False(r.Ok);
         Assert.Contains("唯一", r.Output);
    }

    // ---------- list_directory ----------

    [Fact]
    public async Task ListDirectory_empty_shows_marker()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var sub = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(sub);
        var r = await Tool(reg, "list_directory").ExecuteAsync(
            Args("{\"path\":\"" + sub.Replace("\\", "\\\\") + "\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("(空目录)", r.Output);
    }

    [Fact]
    public async Task ListDirectory_entries_are_sorted()
    {
        var reg = ToolRegistry.CreateBuiltin();
        await File.WriteAllTextAsync(Path.Combine(_dir, "c.txt"), "c");
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "b");
        var r = await Tool(reg, "list_directory").ExecuteAsync(
            Args("{\"path\":\".\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        int ia = r.Output.IndexOf("a.txt", StringComparison.Ordinal);
        int ib = r.Output.IndexOf("b.txt", StringComparison.Ordinal);
        int ic = r.Output.IndexOf("c.txt", StringComparison.Ordinal);
        Assert.True(ia >= 0 && ib >= 0 && ic >= 0);
        Assert.True(ia < ib && ib < ic);
    }

    // ---------- glob ----------

    [Fact]
    public async Task Glob_matches_txt_files()
    {
         var reg = ToolRegistry.CreateBuiltin();
         var sub = Path.Combine(_dir, "src");
         Directory.CreateDirectory(sub);
         await File.WriteAllTextAsync(Path.Combine(sub, "g1.txt"), "x");
         await File.WriteAllTextAsync(Path.Combine(sub, "g2.md"), "x");
         var r = await Tool(reg, "glob").ExecuteAsync(
             Args("{\"pattern\":\"*.txt\"}"), Ctx(), CancellationToken.None);
         Assert.True(r.Ok);
         Assert.Contains("g1.txt", r.Output);
         Assert.DoesNotContain("g2.md", r.Output);
    }

    // ---------- grep ----------

    [Fact]
    public async Task Grep_finds_pattern()
    {
        var reg = ToolRegistry.CreateBuiltin();
        await File.WriteAllTextAsync(Path.Combine(_dir, "s.txt"), "hello world\nfoo bar\n");
        var r = await Tool(reg, "grep_search").ExecuteAsync(
            Args("{\"pattern\":\"hello\",\"path\":\".\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public async Task Grep_invalid_regex_returns_error()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "grep_search").ExecuteAsync(
            Args("{\"pattern\":\"([\",\"path\":\".\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("正则", r.Output);
    }

    // ---------- run_shell ----------

    [Fact]
    public async Task RunShell_echo_succeeds()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "run_shell_command").ExecuteAsync(
            Args("{\"command\":\"echo hello\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public async Task RunShell_empty_command_returns_error()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "run_shell_command").ExecuteAsync(
            Args("{\"command\":\"\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("command 不能为空", r.Output);
    }

    [Fact]
    public async Task RunShell_missing_cwd_returns_error()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "run_shell_command").ExecuteAsync(
            Args("{\"command\":\"echo hi\",\"cwd\":\"Z:\\\\no-such-dir-xyz-123\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("执行目录不存在", r.Output);
    }

    // ---------- memory ----------

    [Fact]
    public async Task Memory_write_then_read_roundtrip()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var w = await Tool(reg, "memory_write").ExecuteAsync(
            Args("{\"name\":\"pref\",\"content\":\"喜欢深色主题\",\"scope\":\"global\"}"), Ctx(), CancellationToken.None);
        Assert.True(w.Ok);
        var r = await Tool(reg, "memory_read").ExecuteAsync(
            Args("{\"name\":\"pref\"}"), Ctx(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("深色", r.Output);
    }

    // ---------- web_fetch ----------

    [Fact]
    public async Task WebFetch_rejects_file_urls()
    {
        var reg = ToolRegistry.CreateBuiltin();
        var r = await Tool(reg, "web_fetch").ExecuteAsync(
            Args("{\"url\":\"file:///etc/passwd\"}"), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("http", r.Output);
    }
}
