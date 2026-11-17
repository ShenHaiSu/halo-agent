#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 删除工具：默认预览、真删前备份、hash 护栏、目录与批量上限，以及备份可被恢复链路读回。
/// </summary>
public sealed class DeleteFileToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;
    private readonly DeleteFileTool _tool = new();

    public DeleteFileToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentDelete_{Guid.NewGuid():N}");
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

    private static ToolCallDto Call(string args) => new("c1", "delete_file", args);

    private static string Sha(string text) =>
        FileToolShared.ComputeHash(Encoding.UTF8.GetBytes(text));

    private string BackupDir => BackupStore.SessionBackupDir(_dataRoot, "ws", "sess");

    private string[] Backups() =>
        Directory.Exists(BackupDir)
            ? Directory.GetFiles(BackupDir, "*.bak")
            : Array.Empty<string>();

    [Fact]
    public async Task DeleteFile_DefaultsToDryRun()
    {
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, "leave me here");

        ToolResult r = await _tool.ExecuteAsync(Call("{\"file_path\":\"scratch.txt\"}"), Ctx());

        Assert.True(r.Success, r.Error);
        Assert.Contains("将删除", r.Output);
        Assert.Contains("dry_run=true", r.Output);
        Assert.True(File.Exists(target));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task DeleteFile_RealDeleteWritesBackup()
    {
        const string content = "payload";
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, content);

        ToolResult r = await _tool.ExecuteAsync(
            Call(
                $"{{\"file_path\":\"scratch.txt\",\"dry_run\":false,\"expected_hash\":\"{Sha(content)}\"}}"
            ),
            Ctx()
        );

        Assert.True(r.Success, r.Error);
        Assert.Contains("已删除", r.Output);
        Assert.False(File.Exists(target));
        Assert.Equal(content.Length, r.FileBytes);

        string[] baks = Backups();
        Assert.Single(baks);
        Assert.True(File.Exists(baks[0] + ".meta.json"));
        Assert.Equal(content, File.ReadAllText(baks[0]));
    }

    [Fact]
    public async Task DeleteFile_HashMismatchRejects()
    {
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, "payload");

        ToolResult r = await _tool.ExecuteAsync(
            Call(
                "{\"file_path\":\"scratch.txt\",\"dry_run\":false,\"expected_hash\":\"deadbeef\"}"
            ),
            Ctx()
        );

        Assert.False(r.Success);
        Assert.Equal("EXPECTED_HASH_MISMATCH", r.ErrorCode);
        Assert.True(File.Exists(target));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task DeleteFile_DirectoryRequiresRecursive()
    {
        string dir = Path.Combine(_workspace, "bundle");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "nested", "b.txt"), "b");

        ToolResult blocked = await _tool.ExecuteAsync(Call("{\"file_path\":\"bundle\"}"), Ctx());
        Assert.False(blocked.Success);
        Assert.Equal("IS_A_DIRECTORY", blocked.ErrorCode);
        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));

        ToolResult removed = await _tool.ExecuteAsync(
            Call("{\"file_path\":\"bundle\",\"recursive\":true,\"dry_run\":false}"),
            Ctx()
        );
        Assert.True(removed.Success, removed.Error);
        Assert.False(Directory.Exists(dir));
        Assert.Equal(2, Backups().Length);
    }

    [Fact]
    public async Task DeleteFile_RecursiveOverLimitRejects()
    {
        string dir = Path.Combine(_workspace, "many");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 51; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i:D2}.txt"), i.ToString());

        ToolResult r = await _tool.ExecuteAsync(
            Call("{\"file_path\":\"many\",\"recursive\":true,\"dry_run\":false}"),
            Ctx()
        );

        Assert.False(r.Success);
        Assert.Equal("RECURSIVE_LIMIT_EXCEEDED", r.ErrorCode);
        Assert.Equal(51, Directory.GetFiles(dir).Length);
        Assert.Empty(Backups());
    }

    [Fact]
    public void CodingPreset_ActivatesDeleteFile()
    {
        var registry = new ToolRegistry(registerBuiltIns: true);
        var active = ToolPreset.ResolveActiveSet(registry.ToolNames, cfg: null);
        Assert.Contains("delete_file", active);
    }

    [Fact]
    public void SubRoleWithFileWrite_RegistersDeleteFile()
    {
        var policy = new SubPolicySnapshot { SubAllowFileWrite = true };
        Assert.Contains("delete_file", ToolRoleRegistries.ForSubRole(policy).ToolNames);
    }

    [Fact]
    public async Task DeleteFile_PassesCentralValidation()
    {
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, "payload");
        var registry = new ToolRegistry(registerBuiltIns: true);

        ToolResult r = await registry.DispatchAsync(
            Call(
                "{\"file_path\":\"scratch.txt\",\"dry_run\":true,\"recursive\":false,\"expected_hash\":null}"
            ),
            Ctx()
        );

        Assert.True(r.Success, r.Error);
        Assert.Contains("将删除", r.Output);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task DeletedFile_CanBeRestoredFromItsBackup()
    {
        const string content = "restore me";
        string target = Path.Combine(_workspace, "scratch.txt");
        File.WriteAllText(target, content);

        ToolResult deleted = await _tool.ExecuteAsync(
            Call(
                $"{{\"file_path\":\"scratch.txt\",\"dry_run\":false,\"expected_hash\":\"{Sha(content)}\"}}"
            ),
            Ctx()
        );
        Assert.True(deleted.Success, deleted.Error);

        string bak = Backups().Single();
        string relativeBak = Path.GetRelativePath(_dataRoot, bak).Replace('\\', '/');
        string args = JsonSerializer.Serialize(new { backup_path = relativeBak });
        ToolResult restored = await new UndoEditTool().ExecuteAsync(
            new ToolCallDto("c2", "undo_edit", args),
            Ctx()
        );

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(content, File.ReadAllText(target));
    }
}
