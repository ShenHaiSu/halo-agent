using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Hooks;
using Xunit;

namespace HaloAgent.Tests;

// need17-06 验收：ToolGuide 硬规则 + 动态装配 + 截断事件 + hook 透传。
public sealed class Stage17PromptLoopTests
{
    private static WorkspaceInfo Ws() => new() { Id = "w", Name = "w", RootPath = Path.GetTempPath() };
    [Fact] public void Guide_Contains_HardRules()
    {
        var t = PromptDefaults.ToolGuideTemplate;
        Assert.Contains("NEVER invent", t); Assert.Contains("verbatim copy-paste", t);
        Assert.Contains("allow_overwrite:true", t); Assert.Contains("dry_run:true", t);
        Assert.Contains("never re-run a broad dump", t);
    }
    [Fact] public void DynamicToolSection_Coding_Has5()
    {
        var s = SystemPromptBuilder.BuildAvailableToolsSection(new HashSet<string>(ToolPreset.Coding, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("read_file", s); Assert.DoesNotContain("skill_tool", s);
    }
    [Fact] public void DynamicToolSection_Full_HasOverlay()
    {
        var reg = new ToolRegistry();
        var all = reg.GetActiveDefinitions(null).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var s = SystemPromptBuilder.BuildAvailableToolsSection(all);
        Assert.Contains("skill_tool", s); Assert.Contains("ask_user", s);
    }
    [Fact] public void Truncated_Event_Factory()
    {
        var e = UnifiedStreamEvent.Truncated(2);
        Assert.Equal(UnifiedStreamEventKind.StreamTruncated, e.Kind);
    }
    [Fact] public async Task Hook_Passthrough()
    {
        var h = new PassThroughToolHook();
        var call = new ToolCallDto("c1", "read_file", "{}");
        var ctx = new ToolContext { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath() };
        var d = await h.BeforeAsync(call, "{}", ctx, CancellationToken.None);
        Assert.False(d.Blocked);
        var r = ToolResult.Ok("ok");
        var after = await h.AfterAsync(call, r, ctx, CancellationToken.None);
        Assert.Same(r, after);
    }
    [Fact] public void Hint_MissingParam_HasDraft()
    {
        var cfg = PromptDefaults.CreateDefault();
        var preview = SystemPromptBuilder.BuildPreview(cfg, Ws(), "py", "2026-01-01");
        Assert.Contains("read_file", preview.AssembledText);
    }
    [Fact] public void Rules_Dedup_NoEmptyLines()
    {
        var s = SystemPromptBuilder.BuildActiveRulesSection(new HashSet<string>(ToolPreset.Coding, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("\n\n", s);
    }
    [Fact] public void ExecFallback_Rule_When_NoGrepList()
    {
        var s = SystemPromptBuilder.BuildActiveRulesSection(new HashSet<string>(new[] { "read_file", "exec_command" }, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("exec_command", s);
    }
}
