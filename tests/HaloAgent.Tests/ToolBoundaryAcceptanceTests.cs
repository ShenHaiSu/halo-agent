#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 工具语义边界的现象级验收：只调用工具并观察返回，不依赖实现细节。
/// </summary>
public sealed class ToolBoundaryAcceptanceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public ToolBoundaryAcceptanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentAccept_{Guid.NewGuid():N}");
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

    private ToolContext Ctx(List<string>? allowed = null) =>
        new()
        {
            WorkspaceRoot = _workspace,
            WorkspaceId = "ws",
            SessionId = "sess",
            DataRoot = _dataRoot,
            AllowedCommandsOverride = allowed,
        };

    private static ToolCallDto Call(string tool, string args) => new("accept", tool, args);

    private static string Sha(string text) =>
        FileToolShared.ComputeHash(Encoding.UTF8.GetBytes(text));

    private ToolResult? Gate(string command, List<string>? allowed = null) =>
        ExecProcessHelper.CheckCmdPolicy(
            command,
            _workspace,
            _workspace,
            Ctx(allowed),
            Stopwatch.StartNew()
        );

    [Fact]
    public async Task H1_OverflowMarkerIsFirstBodyLine()
    {
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "a\nb\nc");
        ToolResult r = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"notes.txt\",\"offset\":99999}"),
            Ctx()
        );
        string[] rows = r.Output.Split('\n');
        Assert.True(r.Success);
        Assert.Contains("OFFSET_OUT_OF_RANGE", rows[1]);
        Assert.Contains("EmptyPage: true", rows[0]);
    }

    [Fact]
    public async Task H2_NormalReadHasNoOverflowMarker()
    {
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "a\nb\nc");
        ToolResult r = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"notes.txt\"}"),
            Ctx()
        );
        Assert.True(r.Success);
        Assert.Contains("1: a", r.Output);
        Assert.DoesNotContain("OFFSET_OUT_OF_RANGE", r.Output);
        Assert.DoesNotContain("EmptyPage", r.Output);
    }

    [Fact]
    public async Task H3_DirectoryInputIsRejectedWithListDirHint()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        ToolResult r = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"src\"}"),
            Ctx()
        );
        Assert.False(r.Success);
        Assert.Equal("IS_A_DIRECTORY", r.ErrorCode);
        Assert.Contains("list_dir", r.Error ?? string.Empty);
    }

    [Fact]
    public async Task H4_MissingFileStillReportsMissing()
    {
        ToolResult r = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"no_such_zzz.txt\"}"),
            Ctx()
        );
        Assert.False(r.Success);
        Assert.Contains("文件不存在", r.Error ?? string.Empty);
        Assert.NotEqual("IS_A_DIRECTORY", r.ErrorCode);
    }

    [Fact]
    public void H5_DeniedCommandListsBuiltinsAndReplaceSemantics()
    {
        ToolResult? r = Gate("del /q x");
        Assert.NotNull(r);
        Assert.Equal("COMMAND_NOT_ALLOWED", r!.ErrorCode);
        Assert.Contains("rd", r.Error ?? string.Empty);
        Assert.Contains("for", r.Error ?? string.Empty);
        Assert.Contains("整表替换", r.Error ?? string.Empty);
    }

    [Fact]
    public void H6_AllowedBuiltinStillPassesPolicyGate()
    {
        Assert.Null(Gate("rd /q no_such_dir"));
    }

    [Fact]
    public async Task H7_DeleteFilePreviewsThenDeletesWithBackup()
    {
        const string content = "temporary";
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, content);
        var tool = new DeleteFileTool();

        ToolResult preview = await tool.ExecuteAsync(
            Call("delete_file", "{\"file_path\":\"scratch.txt\"}"),
            Ctx()
        );
        Assert.True(preview.Success);
        Assert.Contains("将删除", preview.Output);
        Assert.True(File.Exists(target));

        ToolResult done = await tool.ExecuteAsync(
            Call(
                "delete_file",
                $"{{\"file_path\":\"scratch.txt\",\"dry_run\":false,\"expected_hash\":\"{Sha(content)}\"}}"
            ),
            Ctx()
        );
        Assert.True(done.Success, done.Error);
        Assert.Contains("已备份", done.Output);
        Assert.False(File.Exists(target));
        string bakDir = BackupStore.SessionBackupDir(_dataRoot, "ws", "sess");
        Assert.Single(Directory.GetFiles(bakDir, "*.bak"));
    }

    [Fact]
    public async Task H8_InjectionReplay_HasNoNewFalsePositiveOrNegative()
    {
        // 1) 越界：转为带固定标记的空页（新行为）
        File.WriteAllText(Path.Combine(_workspace, "notes.txt"), "a\nb\nc");
        ToolResult overflow = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"notes.txt\",\"offset\":99999}"),
            Ctx()
        );
        Assert.Contains("OFFSET_OUT_OF_RANGE", overflow.Output.Split('\n')[1]);

        // 2) 目录：转为 IS_A_DIRECTORY（新行为）
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        ToolResult dir = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"src\"}"),
            Ctx()
        );
        Assert.Equal("IS_A_DIRECTORY", dir.ErrorCode);

        // 3) 真不存在：仍报文件不存在
        ToolResult missing = await new ReadFileTool().ExecuteAsync(
            Call("read_file", "{\"file_path\":\"this_file_does_not_exist_12345.txt\"}"),
            Ctx()
        );
        Assert.Contains("文件不存在", missing.Error ?? string.Empty);

        // 4) 非图片：仍是 NOT_AN_IMAGE
        File.WriteAllText(Path.Combine(_workspace, "README.md"), "text");
        ToolResult notImage = await new ViewImageTool().ExecuteAsync(
            Call("view_image", "{\"file_path\":\"README.md\"}"),
            Ctx()
        );
        Assert.Equal(ViewImageTool.ErrorCodes.NotAnImage, notImage.ErrorCode);

        // 5) curl：仍拒绝，但文案已列全量
        ToolResult? curl = Gate("curl --version");
        Assert.NotNull(curl);
        Assert.Equal("COMMAND_NOT_ALLOWED", curl!.ErrorCode);
        Assert.Contains("整表替换", curl.Error ?? string.Empty);

        // 6) del：仍拒绝（新行为是文案可自查）
        ToolResult? del = Gate("del /q scratch.txt");
        Assert.NotNull(del);
        Assert.Equal("COMMAND_NOT_ALLOWED", del!.ErrorCode);

        // 7) rd：仍通过策略门
        Assert.Null(Gate("rd /q no_such_dir_policy_probe"));

        // 8) 空 command：仍由中央校验给出 MISSING_REQUIRED_PARAM
        var empty = ToolCallValidator.Validate("exec_command", "{\"command\":\"\"}");
        Assert.False(empty.Success);
        Assert.Equal("MISSING_REQUIRED_PARAM", empty.Code);

        // 9) 不存在的 workdir：仍给出专门文案
        ToolResult badWorkdir = await new ExecCommandTool().ExecuteAsync(
            Call("exec_command", "{\"command\":\"dir /b\",\"workdir\":\"no_such_subdir_abc\"}"),
            Ctx()
        );
        Assert.False(badWorkdir.Success);
        Assert.Contains("工作目录不存在", badWorkdir.Error ?? string.Empty);

        // 10) edit_lines 越界：仍是 OUT_OF_RANGE
        File.WriteAllText(Path.Combine(_workspace, "edit-me.txt"), "one\ntwo");
        ToolResult outOfRange = await new EditLinesTool().ExecuteAsync(
            Call(
                "edit_lines",
                "{\"file_path\":\"edit-me.txt\",\"offset\":99999,\"limit\":1,\"new_text\":\"x\"}"
            ),
            Ctx()
        );
        Assert.False(outOfRange.Success);
        Assert.Contains("OUT_OF_RANGE", outOfRange.Error ?? string.Empty);

        // 11) 清理能力：delete_file 已提供受控路径（无需再借 python os.remove）
        string scratch = Path.Combine(_workspace, "cleanup.txt");
        File.WriteAllText(scratch, "cleanup");
        ToolResult cleaned = await new DeleteFileTool().ExecuteAsync(
            Call(
                "delete_file",
                $"{{\"file_path\":\"cleanup.txt\",\"dry_run\":false,\"expected_hash\":\"{Sha("cleanup")}\"}}"
            ),
            Ctx()
        );
        Assert.True(cleaned.Success, cleaned.Error);
        Assert.False(File.Exists(scratch));

        // 12) undo_edit 越权备份路径：仍被拒绝
        ToolResult escape = await new UndoEditTool().ExecuteAsync(
            Call(
                "undo_edit",
                "{\"backup_path\":\"../../../../Windows/System32/drivers/etc/hosts\"}"
            ),
            Ctx()
        );
        Assert.False(escape.Success);
    }
}
