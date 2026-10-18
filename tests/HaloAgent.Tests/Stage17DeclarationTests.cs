using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

// need17-01 验收：声明层扩展 + strict 双协议 + preset + edit 隐藏 + 别名。
public sealed class Stage17DeclarationTests
{
    [Fact] public void Strict_Serialization_Chat_HasStrict()
    {
        var d = new ToolDefinition("read_file", "x", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        string json = d.ToChatToolJson(StrictToolsMode.Prefer);
        Assert.Contains("\"strict\":true", json);
        string off = d.ToChatToolJson(StrictToolsMode.Off);
        Assert.DoesNotContain("strict", off);
    }

    [Fact] public void Strict_Serialization_Responses_HasStrict()
    {
        var d = new ToolDefinition("read_file", "x", "{\"type\":\"object\",\"properties\":{}}");
        Assert.Contains("\"strict\":true", d.ToResponsesToolJson(StrictToolsMode.Prefer));
        Assert.DoesNotContain("strict", d.ToResponsesToolJson(StrictToolsMode.Off));
    }

    [Fact] public void AllActive_Schemas_Closed()
    {
        var reg = new ToolRegistry();
        foreach (var d in reg.GetDefinitions())
        {
            using var doc = JsonDocument.Parse(d.ParametersJson);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
        foreach (var n in new[] { "read_file", "write_file", "edit_file", "edit_lines", "exec_command" })
        {
            Assert.Contains(reg.ToolNames, x => x == n);
            var d = reg.GetDefinitions().First(x => x.Name == n);
            Assert.Contains("additionalProperties", d.ParametersJson);
        }
    }

    [Fact] public void Preset_Coding_HidesOverlay()
    {
        var reg = new ToolRegistry();
        var active = ToolPreset.ResolveActiveSet(reg.ToolNames, null);
        Assert.DoesNotContain(active, x => x == "ask_user");
        Assert.DoesNotContain(active, x => x == "skill_tool");
        var defs = reg.GetActiveDefinitions(new HashSet<string>(active, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(defs, d => d.Name == "skill_tool");
        Assert.Contains(defs, d => d.Name == "read_file");
    }

    [Fact] public void Edit_HiddenButExecutable()
    {
        var reg = new ToolRegistry();
        var defs = reg.GetActiveDefinitions(null);
        Assert.DoesNotContain(defs, d => d.Name == "edit");
        var editDef = reg.GetDefinitions().First(d => d.Name == "edit");
        Assert.Equal(ToolExposure.Hidden, editDef.Exposure);
        Assert.StartsWith("DEPRECATED", editDef.Description);
    }

    [Fact] public async Task Path_Alias_Accepted_WithWarning()
    {
        var reg = new ToolRegistry();
        var ctx = new ToolContext { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath() };
        string tmp = Path.Combine(Path.GetTempPath(), $"n17_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, "hello");
        try
        {
            var r = await reg.DispatchAsync(new ToolCallDto("c1", "read_file", "{\"path\":\"" + tmp.Replace("\\", "/") + "\"}"), ctx);
            Assert.True(r.Success, r.Error);
            Assert.NotNull(r.ValidatedWarnings);
            Assert.Contains(r.ValidatedWarnings!, w => w.Contains("ALIASED_PARAM"));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact] public async Task Dispatch_HiddenOverlay_Returns_NotEnabled()
    {
        var reg = new ToolRegistry();
        var active = new HashSet<string>(ToolPreset.Coding, StringComparer.OrdinalIgnoreCase);
        var ctx = new ToolContext { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath() };
        var r = await reg.DispatchAsync(new ToolCallDto("c1", "skill_tool", "{\"action\":\"list\"}"), ctx, active);
        Assert.False(r.Success);
        Assert.StartsWith("TOOL_NOT_ENABLED", r.Error);
    }

    [Fact] public void Illegal_Preset_Mix_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            ToolPreset.ValidateOrThrow("Coding", new List<string> { "read_file" }, new List<string> { "+grep_search" }));
    }
}
