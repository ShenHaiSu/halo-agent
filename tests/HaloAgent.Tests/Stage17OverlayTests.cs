using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

// need17-05 验收：overlay Schema 闭合（以 GetDefinition 为准，避免 Validator 过渡态）。
public sealed class Stage17OverlayTests
{
    [Fact]
    public void Coding_Hides_Overlay_Full_Has14()
    {
        var reg = new ToolRegistry();
        var codingActive = new HashSet<string>(ToolPreset.Coding, StringComparer.OrdinalIgnoreCase);
        var coding = reg.GetActiveDefinitions(codingActive);
        Assert.Equal(5, coding.Count);
        var full = reg.GetActiveDefinitions(null);
        Assert.Equal(14, full.Count);
    }

    [Fact]
    public void Ask_Required_Question()
    {
        Assert.Contains("question", new AskUserTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void Skill_Action_Enum()
    {
        var j = new SkillTool().GetDefinition().ParametersJson;
        Assert.Contains("list", j);
        Assert.Contains("get", j);
    }

    [Fact]
    public void Memory_Action_Enum()
    {
        var j = new MemoryTool().GetDefinition().ParametersJson;
        Assert.Contains("set", j);
    }

    [Fact]
    public void Spawn_Task_Required()
    {
        Assert.Contains("task", new SpawnSubAgentTool(true).GetDefinition().ParametersJson);
    }

    [Fact]
    public void Goal_Action_Enum()
    {
        var j = new GoalTool().GetDefinition().ParametersJson;
        Assert.Contains("get", j);
        Assert.Contains("set", j);
    }
}
