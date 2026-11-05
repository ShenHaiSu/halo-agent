 using System.Globalization;
 using System.Text;
 using System.Text.Json;
 using HaloAgent.Engine.Core.Tests.TestSupport;
 using HaloAgent.Engine.Core.Tools;
 using HaloAgent.Engine.Core.Tools.Builtin;
 using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

/// <summary>
/// P25 tool-error-hints 实测问题清单的回归用例（docs/P25-tool-refactory/tool-error-hints-findings.md §4）。
/// </summary>
public sealed class ToolErrorHintsTests
{
    private static ToolContext Ctx(AgentFixture f) => new ToolContext
    {
        Cwd = f.Cwd,
        SessionId = "test",
        Host = new StubHost(f),
        Ct = CancellationToken.None,
    };

    private sealed class StubHost : IToolHost
    {
        private readonly AgentFixture _f;
        public StubHost(AgentFixture f) => _f = f;
        public Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct)
            => Task.FromResult(new UndoSnapshotResult(false, null, null));
        public void PublishToolOutput(string toolCallId, string delta) { }
        public void PublishSilentAlert(string toolCallId, int level) { }
        public HaloAgent.Engine.Abstractions.Types.EffectiveProxy EffectiveProxy =>
            HaloAgent.Engine.Abstractions.Types.EffectiveProxy.None;
        public string? ProxyUrl => null;
        public string DataDir => _f.DataDir;
    }

    private static JsonElement Obj(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // #1 read_file offset:-5 → Failure（问题1）
    [Fact]
    public async Task ReadFile_negative_offset_is_failure()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "a.txt");
        File.WriteAllText(path, "hello\n");
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"offset\":-5}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("offset 不能为负", r.Output);
    }

    // #2 read_file offset:999999 → Failure，不出现 77-76（问题5）
    [Fact]
    public async Task ReadFile_huge_offset_is_failure_with_range()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "b.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 76).Select(i => "line-" + i));
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"offset\":999999}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("超出文件末尾", r.Output);
        Assert.Contains("共 76 行", r.Output);
        Assert.DoesNotContain("77-76", r.Output);
    }

    // #3 read_file limit:-3 → Failure（问题5）
    [Fact]
    public async Task ReadFile_negative_limit_is_failure()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "c.txt");
        File.WriteAllText(path, "hello\n");
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"limit\":-3}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("limit 必须为正整数", r.Output);
    }

    // #4 read_file path=目录 → 目录提示（问题4）
    [Fact]
    public async Task ReadFile_directory_reports_directory_not_missing_file()
    {
        using var f = new AgentFixture();
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(f.Cwd) + "}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("这是一个目录", r.Output);
        Assert.Contains("list_directory", r.Output);
    }

    // #5 read_file path 不存在 → 文件不存在（保持）
    [Fact]
    public async Task ReadFile_missing_still_reports_not_found()
    {
        using var f = new AgentFixture();
        var tool = new ReadFileTool();
        var missing = Path.Combine(f.Cwd, "no-such-file.txt");
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(missing) + "}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("文件不存在", r.Output);
    }

    // #6 edit 父目录不存在 → 中文 Failure（问题4）
    [Fact]
    public async Task Edit_missing_parent_dir_reports_in_chinese()
    {
        using var f = new AgentFixture();
        var tool = new EditTool();
        var target = Path.Combine(f.Cwd, "no-such-dir", "foo.txt");
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(target)
            + ",\"old_string\":\"a\",\"new_string\":\"b\"}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("文件所在目录不存在", r.Output);
    }

    // #6b edit path=目录 → 目录提示
    [Fact]
    public async Task Edit_directory_reports_directory()
    {
        using var f = new AgentFixture();
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(f.Cwd)
            + ",\"old_string\":\"a\",\"new_string\":\"b\"}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("这是一个目录", r.Output);
    }

    // #9 write_file 父目录不存在 → Success + 注明已自动创建（问题3）
    [Fact]
    public async Task WriteFile_creates_parent_and_reports_it()
    {
        using var f = new AgentFixture();
        var tool = new WriteFileTool();
        var target = Path.Combine(f.Cwd, "new-dir-hint", "tool-test.tmp.txt");
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(target)
            + ",\"content\":\"hi\"}"), Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("已自动创建目录", r.Output);
        Assert.True(Directory.Exists(Path.GetDirectoryName(target)));
        Directory.Delete(Path.Combine(f.Cwd, "new-dir-hint"), recursive: true);
    }

    // #10 shell 不存在的命令 → 无 U+FFFD（问题2）
    [Fact]
    public async Task Shell_unknown_command_has_no_replacement_chars()
    {
        using var f = new AgentFixture();
        var tool = new RunShellCommandTool();
        var r = await tool.ExecuteAsync(Obj("{\"command\":\"this-command-does-not-exist-xyz\"}"), Ctx(f), CancellationToken.None);
        Assert.DoesNotContain("\uFFFD", r.Output);
    }

    // #12 grep 非法正则 → Failure 含原因与 offset（问题6）
    [Fact]
    public async Task Grep_invalid_regex_keeps_offset_and_adds_hint()
    {
        using var f = new AgentFixture();
        var tool = new GrepSearchTool();
        var r = await tool.ExecuteAsync(Obj("{\"pattern\":\"[z-a]\"}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("正则表达式无效", r.Output);
        Assert.Contains("at offset 4", r.Output);
        Assert.Contains("字符范围顺序颠倒", r.Output);
    }

    // 校验层：minimum 关键字生效（问题1/5 → 阶段01）
    [Fact]
    public void Validation_rejects_negative_offset_and_limit()
    {
        var schema = ToolRegistry.CreateBuiltin().Get("read_file")!.ParameterSchema;
        var ex1 = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("read_file", schema, Obj("{\"path\":\"a.txt\",\"offset\":-5}")));
        Assert.Contains("offset", ex1.Message);
        Assert.Contains("期望 >=", ex1.Message);

        var ex2 = Assert.Throws<ToolValidationException>(() =>
            ToolArguments.Bind("read_file", schema, Obj("{\"path\":\"a.txt\",\"limit\":-3}")));
        Assert.Contains("limit", ex2.Message);
        Assert.Contains("期望 >=", ex2.Message);
    }
     // 方案 3：DecodeConsoleBytes 先 UTF-8 严格、失败回 GB18030（与 WebFetchTool 同策略）
     [Fact]
     public void DecodeConsoleBytes_handles_gbk_utf8_and_ascii()
     {
         // GBK「中文」（cmd 内建消息风格：0xD6D0 0xCEC4）
         var gbk = new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 };
         Assert.Equal("中文", RunShellCommandTool.DecodeConsoleBytes(gbk, 0, gbk.Length));

         // UTF-8「中文输出测试」（git/node 等无视代码页的程序风格）
         var utf8 = Encoding.UTF8.GetBytes("中文输出测试");
         Assert.Equal("中文输出测试", RunShellCommandTool.DecodeConsoleBytes(utf8, 0, utf8.Length));

         // 纯 ASCII：两种解码结果相同，原样保留
         var ascii = Encoding.ASCII.GetBytes("hello (exit 1)\r\n");
         Assert.Equal("hello (exit 1)\r\n", RunShellCommandTool.DecodeConsoleBytes(ascii, 0, ascii.Length));

         Assert.Equal(string.Empty, RunShellCommandTool.DecodeConsoleBytes(Array.Empty<byte>(), 0, 0));
     }

     // 端到端：无视代码页、固定输出 UTF-8 的程序（git/node 风格），中文不再变锟斤拷
     [Fact]
     public async Task Shell_utf8_program_output_decodes_correctly()
     {
         using var f = new AgentFixture();
         var tool = new RunShellCommandTool();
         string cmd;
         if (OperatingSystem.IsWindows())
         {
             // PowerShell 显式按 UTF-8 写管道
             cmd = "powershell -NoProfile -NonInteractive -Command \"[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Write-Output '中文输出测试'\"";
         }
         else
         {
             // printf 直接发原始 UTF-8 字节，不依赖 locale
             cmd = "printf '\\xe4\\xb8\\xad\\xe6\\x96\\x87\\xe8\\xbe\\x93\\xe5\\x87\\xba\\xe6\\xb5\\x8b\\xe8\\xaf\\x95\\n'";
         }
         var r = await tool.ExecuteAsync(Obj("{\"command\":" + JsonSerializer.Serialize(cmd) + "}"), Ctx(f), CancellationToken.None);
         Assert.DoesNotContain("\uFFFD", r.Output);
         Assert.Contains("中文输出测试", r.Output);
     }

     // 端到端：cmd 内建输出（GBK 风格），中文依然正确——仅中文控制台（OEM 936）可测
     [Fact]
     public async Task Shell_cmd_builtin_chinese_still_decodes_correctly()
     {
         if (!OperatingSystem.IsWindows() || CultureInfo.CurrentCulture.TextInfo.OEMCodePage != 936)
             return;
         using var f = new AgentFixture();
         var tool = new RunShellCommandTool();
         var r = await tool.ExecuteAsync(Obj("{\"command\":" + JsonSerializer.Serialize("echo 中文测试") + "}"), Ctx(f), CancellationToken.None);
         Assert.DoesNotContain("\uFFFD", r.Output);
         Assert.Contains("中文测试", r.Output);
     }
}
