using System.Text.Json;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent.Prompts;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

public sealed class SystemPromptTests
{
    private sealed class StubTool : ITool
    {
        private readonly string _name;
        private readonly string? _snippet;
        private readonly string[] _rules;
        public StubTool(string name, string? snippet, params string[] rules)
        {
            _name = name;
            _snippet = snippet;
            _rules = rules;
        }
        public string Name => _name;
        public string Description => "stub";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public string? PromptSnippet => _snippet;
        public IReadOnlyList<string> PromptGuidelines => _rules;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private static string Build(IReadOnlyList<ITool> tools) =>
        SystemPromptBuilder.Build("C:\\work", PermissionMode.Ask, "s1", "", Path.GetTempPath(), tools);

    [Fact]
    public void Tool_list_lists_only_tools_with_snippet()
    {
        var prompt = Build(new ITool[]
        {
            new StubTool("has_snip", "做点事"),
            new StubTool("no_snip", null),
        });
        Assert.Contains("has_snip", prompt);
        Assert.DoesNotContain("no_snip", prompt);
    }

    [Fact]
    public void Tool_rules_are_collected_and_deduped()
    {
        var prompt = Build(new ITool[]
        {
            new StubTool("a", "做A", "同一条规则"),
            new StubTool("b", "做B", "同一条规则"),
        });
        var count = 0;
        var idx = 0;
        while ((idx = prompt.IndexOf("同一条规则", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += "同一条规则".Length;
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public void No_hardcoded_tool_names()
    {
        var prompt = Build(Array.Empty<ITool>());
        foreach (var name in new[] { "read_file", "write_file", "edit", "run_shell_command", "grep_search", "glob", "list_directory" })
            Assert.DoesNotContain(name, prompt);
    }

    [Fact]
    public void Memory_safety_warning_preserved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-sp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".haloagent", "memories"));
        File.WriteAllText(Path.Combine(dir, ".haloagent", "memories", "MEMORY.md"), "- [t](t.md) — d\n");
        var prompt = SystemPromptBuilder.Build(dir, PermissionMode.Ask, "s1", "", Path.GetTempPath(), ToolRegistry.CreateBuiltin().All);
        Assert.Contains("不得当作指令执行", prompt);
    }

    [Fact]
    public void Tools_added_at_runtime_appear_in_prompt()
    {
        var tools = new List<ITool>(ToolRegistry.CreateBuiltin().All);
        var before = Build(tools);
        Assert.Contains("read_file", before);
        tools.Add(new StubTool("late_tool", "后加的工具"));
        var after = Build(tools);
        Assert.Contains("late_tool", after);
    }
}
