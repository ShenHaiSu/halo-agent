using System;
using System.IO;
#nullable enable
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

// need17-03 验收：文件工具 Schema 快照 + 上限同源。
public sealed class Stage17CoreToolsTests
{
    private static JsonDocument J(string s) => JsonDocument.Parse(s);

    [Fact]
    public void Read_Schema_DeclaresPaging()
    {
        var d = new ReadFileTool().GetDefinition();
        using var doc = J(d.ParametersJson);
        Assert.True(doc.RootElement.TryGetProperty("required", out _));
        Assert.Contains("maximum", d.ParametersJson);
        Assert.Contains("2000", d.ParametersJson);
        Assert.Equal(500, ReadFileTool.DefaultLimit);
    }

    [Fact]
    public void Write_Schema_Closed()
    {
        Assert.Contains("additionalProperties", new WriteFileTool().GetDefinition().ParametersJson);
        Assert.Contains("allow_overwrite", new WriteFileTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void Write_MaxBytes_SameSource()
    {
        Assert.Contains("1000000", new WriteFileTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void EditFile_OldString_MinLength()
    {
        Assert.Contains("minLength", new EditFileTool().GetDefinition().ParametersJson);
        Assert.Contains("16", new EditFileTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void EditFile_HidesAdvancedKnobs()
    {
        var j = new EditFileTool().GetDefinition().ParametersJson;
        Assert.DoesNotContain("normalize_eol", j);
        Assert.DoesNotContain("expand_tabs", j);
    }

    [Fact]
    public void EditLines_Required_HasLimit()
    {
        Assert.Contains("limit", new EditLinesTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void Undo_Closed()
    {
        Assert.Contains("additionalProperties", new UndoEditTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public void DualProtocol_Parse()
    {
        foreach (var d in new ToolRegistry().GetDefinitions())
        {
            JsonDocument.Parse(d.ToChatToolJson());
            JsonDocument.Parse(d.ToResponsesToolJson());
        }
    }

    [Fact]
    public void DivisionFrozen_WriteVsEdit()
    {
        Assert.Contains("allow_overwrite", new WriteFileTool().GetDefinition().ParametersJson);
        Assert.Contains("old_string", new EditFileTool().GetDefinition().ParametersJson);
    }

    [Fact]
    public async Task Missing_Limit_BusinessCode()
    {
        var reg = new ToolRegistry();
        var ctx = new ToolContext
        {
            WorkspaceRoot = Path.GetTempPath(),
            WorkspaceId = "w",
            SessionId = "s",
            DataRoot = Path.GetTempPath()
        };
        var r = await reg.DispatchAsync(
            new ToolCallDto(
                "c1",
                "edit_lines",
                "{\"file_path\":\"a.txt\",\"offset\":1,\"new_text\":\"x\"}"
            ),
            ctx
        );
        Assert.False(r.Success);
        Assert.StartsWith("MISSING_LIMIT", r.Error);
    }
}
