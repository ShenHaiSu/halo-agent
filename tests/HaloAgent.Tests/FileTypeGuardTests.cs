#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 文件与目录的类型边界：传目录必须返回明确的码与指路文案，不能被误报成"文件不存在"或"类型不支持"。
/// </summary>
public sealed class FileTypeGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public FileTypeGuardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentFileType_{Guid.NewGuid():N}");
        _workspace = Path.Combine(_tempRoot, "ws");
        _dataRoot = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 忽略测试临时目录清理异常
        }
    }

    private ToolContext Ctx() =>
        new()
        {
            WorkspaceRoot = _workspace,
            WorkspaceId = "ws",
            SessionId = "sess",
            DataRoot = _dataRoot,
        };

    [Fact]
    public async Task ReadFile_Directory_ReturnsIsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "subdir"));

        ToolResult result = await new ReadFileTool().ExecuteAsync(
            new ToolCallDto("c1", "read_file", "{\"file_path\":\"subdir\"}"),
            Ctx()
        );

        Assert.False(result.Success);
        Assert.Equal("IS_A_DIRECTORY", result.ErrorCode);
        Assert.Contains("list_dir", result.Error ?? string.Empty);
    }

    [Fact]
    public async Task ReadFile_MissingFile_StillReportsMissing()
    {
        ToolResult result = await new ReadFileTool().ExecuteAsync(
            new ToolCallDto("c1", "read_file", "{\"file_path\":\"no_such_file_zzz.txt\"}"),
            Ctx()
        );

        Assert.False(result.Success);
        Assert.Contains("文件不存在", result.Error ?? string.Empty);
        Assert.NotEqual("IS_A_DIRECTORY", result.ErrorCode);
    }

    [Fact]
    public async Task ViewImage_Directory_ReturnsIsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "images"));

        ToolResult result = await new ViewImageTool().ExecuteAsync(
            new ToolCallDto("c1", "view_image", "{\"file_path\":\"images\"}"),
            Ctx()
        );

        Assert.False(result.Success);
        Assert.Equal("IS_A_DIRECTORY", result.ErrorCode);
        Assert.Contains("list_dir", result.Error ?? string.Empty);
    }

    [Fact]
    public async Task ViewImage_DirectoryWithImageExtension_ReturnsIsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "looks-like-image.png"));

        ToolResult result = await new ViewImageTool().ExecuteAsync(
            new ToolCallDto("c1", "view_image", "{\"file_path\":\"looks-like-image.png\"}"),
            Ctx()
        );

        Assert.False(result.Success);
        Assert.Equal("IS_A_DIRECTORY", result.ErrorCode);
    }

    [Fact]
    public async Task ViewImage_NonImageFile_StillReportsNotAnImage()
    {
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "plain text");

        ToolResult result = await new ViewImageTool().ExecuteAsync(
            new ToolCallDto("c1", "view_image", "{\"file_path\":\"notes.txt\"}"),
            Ctx()
        );

        Assert.False(result.Success);
        Assert.Equal(ViewImageTool.ErrorCodes.NotAnImage, result.ErrorCode);
    }
}
