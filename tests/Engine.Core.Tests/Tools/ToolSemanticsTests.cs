using System.Text.Json;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Core.Tools.Builtin;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

public sealed class ToolSemanticsTests
{
    private static ToolContext Ctx(AgentFixture f, string? toolCallId = null) => new ToolContext
    {
        Cwd = f.Cwd,
        SessionId = "test",
        Host = new StubHost(f),
        Ct = CancellationToken.None,
        ToolCallId = toolCallId,
    };

    private sealed class StubHost : IToolHost
    {
        private readonly AgentFixture _f;
        public StubHost(AgentFixture f) => _f = f;
        public List<string> Backups = new();
        public Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct)
        {
            Backups.Add(path);
            return Task.FromResult(new UndoSnapshotResult(false, null, null));
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

    [Fact]
    public async Task ReadFile_gives_offset_hint_when_truncated()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "big.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 5000).Select(i => "line-" + i));
        var tool = new ReadFileTool();
        var r = await tool.ExecuteAsync(Obj("{\"path\":\"" + path.Replace("\\", "\\\\") + "\"}"), Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("用 offset=", r.Output);
    }

     [Fact]
     public async Task ReadFile_default_limit_is_2000()
     {
         using var f = new AgentFixture();
         var path = Path.Combine(f.Cwd, "big2.txt");
         File.WriteAllLines(path, Enumerable.Range(1, 3000).Select(i => "line-" + i));
         var tool = new ReadFileTool();
         var r = await tool.ExecuteAsync(Obj("{\"path\":\"" + path.Replace("\\", "\\\\") + "\"}"), Ctx(f), CancellationToken.None);
         Assert.True(r.Ok);
         Assert.Contains($"用 offset={ToolLimits.ReadDefaultMaxLines} 继续读取", r.Output);
     }

    [Fact]
    public async Task Edit_no_change_is_rejected()
    {
        using var f = new AgentFixture();
        var path = Path.Combine(f.Cwd, "e.txt");
        File.WriteAllText(path, "hello");
        var host = new StubHost(f);
        var ctx = new ToolContext { Cwd = f.Cwd, SessionId = "test", Host = host, Ct = CancellationToken.None };
        var tool = new EditTool();
        var esc = path.Replace("\\", "\\\\");
        var r = await tool.ExecuteAsync(Obj("{\"path\":\"" + esc + "\",\"old_string\":\"hello\",\"new_string\":\"hello\"}"), ctx, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("未做修改:", r.Output);
        Assert.Empty(host.Backups);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public async Task ListDirectory_honours_limit_and_reports_total()
    {
        using var f = new AgentFixture();
        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(f.Cwd, "f" + i + ".txt"), "x");
        var tool = new ListDirectoryTool();
        var esc = f.Cwd.Replace("\\", "\\\\");
        var r = await tool.ExecuteAsync(Obj("{\"path\":\"" + esc + "\",\"limit\":10}"), Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("共 20 项", r.Output);
        Assert.Contains("还有 10 项未显示", r.Output);
    }

    [Fact]
    public async Task Glob_honours_limit_param()
    {
        using var f = new AgentFixture();
        var sub = Path.Combine(f.Cwd, "src");
        Directory.CreateDirectory(sub);
        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(sub, "g" + i + ".txt"), "x");
        var tool = new GlobTool();
        var r = await tool.ExecuteAsync(Obj("{\"pattern\":\"*.txt\",\"limit\":5}"), Ctx(f), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("还有 15 个未显示", r.Output);
    }

    [Fact]
    public void WebFetch_requires_confirm_in_ask_mode()
    {
        var d = PermissionChecker.Check(PermissionMode.Ask, PermissionChecker.Operation.Network, null, interactive: true);
        Assert.Equal(PermDecisionKind.NeedAsk, d.Kind);
        var d2 = PermissionChecker.Check(PermissionMode.Ask, PermissionChecker.Operation.Network, null, interactive: false);
        Assert.Equal(PermDecisionKind.Deny, d2.Kind);
    }

    [Fact]
    public void Descriptions_use_tool_limits()
    {
        var all = ToolRegistry.CreateBuiltin().All;
        var read = all.First(t => t.Name == "read_file");
        Assert.Contains(ToolLimits.ReadMaxChars.ToString(), read.Description);
        var grep = all.First(t => t.Name == "grep_search");
        Assert.Contains("limit", grep.ParameterSchema.GetRawText());
        var glob = all.First(t => t.Name == "glob");
        Assert.Contains("limit", glob.ParameterSchema.GetRawText());
        var ls = all.First(t => t.Name == "list_directory");
        Assert.Contains("limit", ls.ParameterSchema.GetRawText());
    }
}
