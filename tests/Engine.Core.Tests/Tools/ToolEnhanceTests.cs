using System.Text.Json;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Agent.Prompts;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Core.Tools.Builtin;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

/// <summary>
/// 工具增强回归：行号/换行口径、失败结构化回包、参数统一、解析加固、乐观并发、窗口收敛、投影裁剪、搜索上下文。
/// </summary>
public sealed class ToolEnhanceTests
{
    private static ToolContext Ctx(AgentFixture f, ToolArguments? typed = null) => new ToolContext
    {
        Cwd = f.Cwd,
        SessionId = "test",
        Host = new StubHost(f),
        Ct = CancellationToken.None,
        TypedArgs = typed,
    };

    private sealed class StubHost : IToolHost
    {
        private readonly AgentFixture _f;
        public StubHost(AgentFixture f) => _f = f;
        public List<string> Backups = new();
        public Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct)
        {
            if (!File.Exists(path)) return Task.FromResult(new UndoSnapshotResult(false, null, null));
            Backups.Add(path);
            return Task.FromResult(new UndoSnapshotResult(true, "snap", null));
        }
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

    private static ToolArguments Bind(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ToolArguments.Bind(tool.Name, tool.ParameterSchema, doc.RootElement.Clone());
    }

    // ---------- A1：串行标记 ----------

    [Fact]
    public void Edit_and_write_are_sequential()
    {
        Assert.Equal(ToolExecutionMode.Sequential, new EditTool().ExecutionMode);
        Assert.Equal(ToolExecutionMode.Sequential, new WriteFileTool().ExecutionMode);
    }

    // ---------- A2：多命中带行号且不写 ----------

    [Fact]
    public async Task Edit_multi_hit_reports_lines_and_does_not_write()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "multi.txt");
        File.WriteAllText(path, "a foo b\nc foo d\ne foo f\n");
        var tool = new EditTool();
        var before = File.ReadAllText(path);
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"foo\",\"new_string\":\"bar\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("3 次", r.Output);
        Assert.Contains("第 1-1 行", r.Output);
        Assert.Contains("第 2-2 行", r.Output);
        Assert.Contains("第 3-3 行", r.Output);
        Assert.Contains("read_file", r.Output);
        Assert.Equal(before, File.ReadAllText(path));
    }

    // ---------- A3：零命中最小回包 ----------

    [Fact]
    public async Task Edit_zero_hit_crlf_reports_newline_and_pointer()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "crlf.txt");
        File.WriteAllText(path, "hello\r\nworld\r\n");
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"zzz\",\"new_string\":\"b\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("0 匹配", r.Output);
        Assert.Contains("共 2 行", r.Output);
        Assert.Contains("CRLF", r.Output);
        Assert.Contains("\\r\\n", r.Output);
        Assert.Contains("read_file", r.Output);
        Assert.Contains("hash", r.Output);
    }

    [Fact]
    public async Task Edit_zero_hit_bom_reports_bom_hint()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "bom.txt");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("hello\n")).ToArray();
        File.WriteAllBytes(path, withBom);
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"zzz\",\"new_string\":\"b\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("BOM", r.Output);
    }

    [Fact]
    public async Task Edit_zero_hit_truncated_view_reports_reread_hint()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "trunc.txt");
        var longLine = new string('x', ToolLimits.ReadMaxLineChars + 10);
        File.WriteAllText(path, "ok\n" + longLine + "\n");
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"zzz\",\"new_string\":\"b\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("截断", r.Output);
    }

    // ---------- A4：口径 ----------

    [Fact]
    public async Task Read_header_reports_newline_and_offset_semantics()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "h.txt");
        File.WriteAllText(path, "a\nb\nc\n");
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":" + JsonSerializer.Serialize(path) + "}"), Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("共 3 行", r.Output);
        Assert.Contains("换行", r.Output);
        Assert.Contains("行号从 1 起", r.Output);
        Assert.Contains("offset 从 0 起", r.Output);
        Assert.Contains("不是文件内容", r.Output);
    }

    [Fact]
    public async Task Read_empty_path_is_rejected_before_resolve()
    {
        using var f = new AgentFixture();
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":\"\"}"), Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("path 不能为空", r.Output);
    }

    [Fact]
    public async Task Read_visible_whitespace_renders_markers()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "ws.txt");
        File.WriteAllText(path, "a b\tc\n");
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"visible_whitespace\":true}"),
            Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("·", r.Output);
        Assert.Contains("⇥", r.Output);
        Assert.Contains("$", r.Output);
    }

    [Fact]
    public void Prompt_contains_workflow_without_hardcoding_in_empty_registry()
    {
        var prompt = SystemPromptBuilder.Build(
            "C:\\work", HaloAgent.Engine.Abstractions.Types.PermissionMode.Ask,
            "s1", "", Path.GetTempPath(), ToolRegistry.CreateBuiltin().All);
        Assert.Contains("小窗口", prompt);
        Assert.Contains("截断", prompt);
    }

    // ---------- A5：成功回包 ----------

    [Fact]
    public async Task Edit_success_reports_hash_and_surrounding()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "ok.txt");
        File.WriteAllText(path, "line1\nhello foo world\nline3\n");
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"foo\",\"new_string\":\"bar\"}"),
            Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("第 2-2 行", r.Output);
        Assert.Contains("hash", r.Output);
        Assert.Contains("bar", r.Output);
        Assert.True(r.Output.Length <= 2000);
    }

    [Fact]
    public async Task Write_success_reports_bytes_lines_hash()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "w.txt");
        var tool = new WriteFileTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"content\":\"a\\nb\\n\"}"),
            Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("字节", r.Output);
        Assert.Contains("2 行", r.Output);
        Assert.Contains("hash", r.Output);
    }

    // ---------- B1：空串语义 ----------

    [Fact]
    public async Task Edit_delete_with_empty_new_string_succeeds_via_typed_args()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "del.txt");
        File.WriteAllText(path, "hello [DEL] world");
        var tool = new EditTool();
        var typed = Bind(tool, "{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\" [DEL]\",\"new_string\":\"\"}");
        Assert.Equal(string.Empty, typed.RequiredString("new_string"));
        var r = await tool.ExecuteAsync(Obj("{}"), Ctx(f, typed), CancellationToken.None);
        Assert.True(r.Ok, r.Output);
        Assert.Equal("hello world", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_empty_old_string_is_still_rejected()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "e.txt");
        File.WriteAllText(path, "hello");
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"\",\"new_string\":\"x\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("old_string 不能为空", r.Output);
    }

    [Fact]
    public async Task Edit_same_old_new_is_rejected()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "s.txt");
        File.WriteAllText(path, "hello");
        var tool = new EditTool();
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"old_string\":\"hello\",\"new_string\":\"hello\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("未做修改", r.Output);
    }

    // ---------- B2：解析器 ----------

    [Fact]
    public void Parser_accepts_fence_wrapped_json()
    {
        var parsed = ToolArgumentParser.Parse("```json\n{\"path\":\"a.txt\"}\n```");
        Assert.Equal("a.txt", parsed.GetProperty("path").GetString());
    }

    [Fact]
    public void Parser_accepts_trailing_comma()
    {
        var parsed = ToolArgumentParser.Parse("{\"path\":\"a.txt\",}");
        Assert.Equal("a.txt", parsed.GetProperty("path").GetString());
    }

    [Fact]
    public void Parser_bare_newline_reports_line_number()
    {
        var ex = Assert.Throws<ToolArgumentException>(() =>
            ToolArgumentParser.Parse("{\"path\":\"a.txt\",\n\"content\":\"line1\nline2\"}"));
        Assert.Contains("换行", ex.Message);
    }

    [Fact]
    public void Parser_truncation_budget_wins_over_repair()
    {
        var big = "{\"path\":\"" + new string('a', 9000) + "\"";
        var ex = Assert.Throws<ToolArgumentException>(() =>
            ToolArgumentParser.Parse(big, suspectedTruncationBudget: 8000));
        Assert.Contains("截断", ex.Message);
    }

    [Fact]
    public void Parser_does_not_cut_braces_inside_code()
    {
        var raw = "please run {\"command\":\"echo {}\"} thanks";
        var parsed = ToolArgumentParser.Parse(raw);
        Assert.Equal("echo {}", parsed.GetProperty("command").GetString());
    }

    [Fact]
    public void Parser_single_quote_fallback_keeps_payload_quotes()
    {
        var parsed = ToolArgumentParser.Parse("{'path':'a.txt','content':'it is ok'}");
        Assert.Equal("a.txt", parsed.GetProperty("path").GetString());
        Assert.Equal("it is ok", parsed.GetProperty("content").GetString());
    }

    // ---------- B3：乐观并发 ----------

    [Fact]
    public async Task Write_expected_hash_mismatch_rejects_and_keeps_file()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "cas.txt");
        File.WriteAllText(path, "v1\n");
        var tool = new WriteFileTool();
        // 先读一次拿到真 hash（经 edit 成功回包口径计算）
        var goodHash = HaloAgent.Engine.Core.Tools.Builtin.EditFeedback.ComputeShortHash("v1\n");
        var r = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"content\":\"v2\",\"expectedHash\":\"deadbeef\"}"),
            Ctx(f), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("已被改动", r.Output);
        Assert.Equal("v1\n", File.ReadAllText(path));

        var r2 = await tool.ExecuteAsync(
            Obj("{\"path\":" + JsonSerializer.Serialize(path) + ",\"content\":\"v2\",\"expectedHash\":" + JsonSerializer.Serialize(goodHash) + "}"),
            Ctx(f), CancellationToken.None);
        Assert.True(r2.Ok);
        Assert.Equal("v2", File.ReadAllText(path));
    }

    // ---------- C1：默认窗口 ----------

    [Fact]
    public void Read_default_window_is_small()
    {
        Assert.True(ToolLimits.ReadDefaultMaxLines <= 400);
        var read = ToolRegistry.CreateBuiltin().All.First(t => t.Name == "read_file");
        Assert.Contains(ToolLimits.ReadDefaultMaxLines.ToString(), read.Description);
    }

    // ---------- C2：投影裁剪默认关 ----------

    [Fact]
    public void Context_prune_is_off_by_default_and_disk_stays_full()
    {
        using var f = new AgentFixture();
        var big = new string('z', 5000);
         var msgs = new List<HaloAgent.Engine.Core.Llm.ChatMessage>
         {
             HaloAgent.Engine.Core.Llm.ChatMessage.System("sys"),
             HaloAgent.Engine.Core.Llm.ChatMessage.Tool("c1", big),
             HaloAgent.Engine.Core.Llm.ChatMessage.Tool("c2", big),
             HaloAgent.Engine.Core.Llm.ChatMessage.Tool("c3", big),
         };
         var off = ContextProjector.ProjectForLlm(msgs, enabled: false);
         Assert.Equal(big, off[1].Content);
         var on = ContextProjector.ProjectForLlm(msgs, enabled: true);
         Assert.Contains("已裁剪", on[1].Content);
         Assert.Equal(big, on[3].Content);
         Assert.Equal(big, msgs[1].Content);
     }
 
     // ---------- C3：搜索上下文 ----------
 
     [Fact]
    public async Task Grep_context_lines_include_neighbors()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "g.txt");
        File.WriteAllText(path, "l1\nl2 hit\nl3\n");
        var tool = new GrepSearchTool();
        var r0 = await tool.ExecuteAsync(
            Obj("{\"pattern\":\"hit\",\"path\":" + JsonSerializer.Serialize(f.Cwd) + "}"),
            Ctx(f), CancellationToken.None);
        Assert.True(r0.Ok);
        Assert.Contains("l2 hit", r0.Output);
        Assert.DoesNotContain("l1", r0.Output);

        var typed = Bind(tool, "{\"pattern\":\"hit\",\"path\":" + JsonSerializer.Serialize(f.Cwd) + ",\"context\":1}");
        var r1 = await tool.ExecuteAsync(Obj("{}"), Ctx(f, typed), CancellationToken.None);
        Assert.True(r1.Ok);
        Assert.Contains("l1", r1.Output);
        Assert.Contains("l3", r1.Output);
    }
}
