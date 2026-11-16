using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

// read_file 越界分页：保持成功包形与中文提示，附加机器可读空页标记。
public sealed class ReadOverflowTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly ToolContext _context;
    private readonly ReadFileTool _tool = new();

    public ReadOverflowTests()
    {
        _workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentReadOverflow_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_workspaceRoot);
        _context = new ToolContext
        {
            WorkspaceRoot = _workspaceRoot,
            WorkspaceId = "w",
            SessionId = "s",
            DataRoot = Path.GetTempPath(),
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspaceRoot))
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
        }
        catch
        {
            // 忽略测试临时目录清理异常
        }
    }

    [Fact]
    public async Task OffsetBeyondTotalLines_KeepsOkShapeAndReportsEmptyPage()
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, "notes.txt"), "line1\nline2\nline3");

        ToolResult r = await _tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "read_file",
                "{\"file_path\":\"notes.txt\",\"offset\":99,\"limit\":10}"
            ),
            _context
        );

        Assert.True(r.Success);
        Assert.Contains("超出文件总行数", r.Output);
        Assert.Contains("无内容返回", r.Output);
        Assert.Contains("returned_lines:0", r.Output);
        Assert.Contains("total_lines:3", r.Output);
    }

    [Fact]
    public async Task OffsetAtLastLine_ReturnsContentWithoutEmptyMarker()
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, "notes.txt"), "line1\nline2\nline3");

        ToolResult r = await _tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "read_file",
                "{\"file_path\":\"notes.txt\",\"offset\":3,\"limit\":10}"
            ),
            _context
        );

        Assert.True(r.Success);
        Assert.Contains("line3", r.Output);
        Assert.DoesNotContain("returned_lines:0", r.Output);
    }

    [Fact]
    public async Task OffsetOverflow_HasExplicitMarkerOnFirstBodyLine()
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, "notes.txt"), "line1\nline2\nline3");

        ToolResult r = await _tool.ExecuteAsync(
            new ToolCallDto(
                "c1",
                "read_file",
                "{\"file_path\":\"notes.txt\",\"offset\":99,\"limit\":10}"
            ),
            _context
        );

        Assert.True(r.Success);
        string[] rows = r.Output.Split('\n');
        Assert.Contains("OffsetRequested: 99", rows[0]);
        Assert.Contains("EmptyPage: true", rows[0]);
        Assert.Contains("OFFSET_OUT_OF_RANGE", rows[1]);
        Assert.DoesNotContain("EmptyPage", rows[1]);
    }

    [Fact]
    public async Task NormalRead_HasNoEmptyPageMarker()
    {
        File.WriteAllText(Path.Combine(_workspaceRoot, "notes.txt"), "line1\nline2\nline3");

        ToolResult r = await _tool.ExecuteAsync(
            new ToolCallDto("c1", "read_file", "{\"file_path\":\"notes.txt\"}"),
            _context
        );

        Assert.True(r.Success);
        Assert.DoesNotContain("EmptyPage", r.Output);
        Assert.DoesNotContain("OFFSET_OUT_OF_RANGE", r.Output);
    }
}
