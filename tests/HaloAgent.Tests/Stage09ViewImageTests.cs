#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-05/08 §1：view_image 路径/类型/魔数/越权/去重。</summary>
public sealed class Stage09ViewImageTests : IDisposable
{
    // 1x1 PNG（68 字节，IHDR 1x1）
    private const string Png1x1Base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public Stage09ViewImageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09View_{Guid.NewGuid():N}");
        _workspace = Path.Combine(_tempRoot, "ws");
        _dataRoot = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllBytes(Path.Combine(_workspace, "a.png"), Convert.FromBase64String(Png1x1Base64));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private ToolContext Ctx() => new()
    {
        WorkspaceRoot = _workspace,
        WorkspaceId = "ws_test",
        SessionId = "sess_test",
        DataRoot = _dataRoot,
    };

    private static ToolCallDto Call(string argsJson) => new("call_1", "view_image", argsJson);

    [Fact(DisplayName = "S09-VI1: 合法 png 读取成功并落盘引用")]
    public async Task ValidPng_Succeeds_WithRef()
    {
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"a.png\"}"), Ctx());
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Images);
        Assert.Single(result.Images!);
        var img = result.Images![0];
        Assert.StartsWith("img_", img.ImageId);
        Assert.Equal(16, img.Sha16.Length);
        Assert.Equal("image/png", img.Mime);
        Assert.Equal(1, img.Width);
        Assert.Equal(1, img.Height);
        Assert.Contains("已读取", result.Output);
        string abs = Path.Combine(_dataRoot, "sessions", "ws_test", "sess_test",
            img.RelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs));
        Assert.DoesNotContain("base64", result.Output);
    }

    [Fact(DisplayName = "S09-VI2: 同一文件 sha16 去重（ImageId 一致，单文件）")]
    public async Task SameFile_Dedupes_BySha16()
    {
        var tool = new ViewImageTool();
        var r1 = await tool.ExecuteAsync(Call("{\"path\":\"a.png\"}"), Ctx());
        File.Copy(Path.Combine(_workspace, "a.png"), Path.Combine(_workspace, "b.png"), overwrite: true);
        var r2 = await tool.ExecuteAsync(Call("{\"path\":\"b.png\"}"), Ctx());
        Assert.True(r1.Success && r2.Success);
        Assert.Equal(r1.Images![0].ImageId, r2.Images![0].ImageId);
        string[] files = Directory.GetFiles(
            Path.Combine(_dataRoot, "sessions", "ws_test", "sess_test", "attachments"), "*", SearchOption.AllDirectories);
        Assert.Single(files);
    }

    [Fact(DisplayName = "S09-VI3: ../ 路径遍历拒绝")]
    public async Task TraversalPath_Rejected()
    {
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"../secret.png\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("PATH_TRAVERSAL", result.Error);
    }

    [Fact(DisplayName = "S09-VI4: gif 类型拒绝（扩展名+魔数）")]
    public async Task GifType_Rejected()
    {
        byte[] gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0, 0, 0 };
        File.WriteAllBytes(Path.Combine(_workspace, "a.gif"), gif);
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"a.gif\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("不支持的图片类型", result.Error);
    }

    [Fact(DisplayName = "S09-VI5: 伪装 png（魔数不符）拒绝")]
    public async Task FakePng_MagicMismatch_Rejected()
    {
        File.WriteAllText(Path.Combine(_workspace, "fake.png"), "this is not an image");
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"fake.png\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("文件头校验失败", result.Error);
    }

    [Fact(DisplayName = "S09-VI6: 系统目录拒绝（存在性检查之前）")]
    public async Task SystemDirectory_Rejected()
    {
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"C:\\\\Windows\\\\System32\\\\x.png\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("系统关键目录", result.Error);
    }

    [Fact(DisplayName = "S09-VI7: _index.json 拒绝直读")]
    public async Task IndexFile_Rejected()
    {
        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\".haloagent/skills/_index.json\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("PATH_TRAVERSAL", result.Error);
    }

    [Fact(DisplayName = "S09-VI8: 空 path 与缺文件 Fail")]
    public async Task EmptyAndMissing_Fail()
    {
        var tool = new ViewImageTool();
        var empty = await tool.ExecuteAsync(Call("{}"), Ctx());
        Assert.False(empty.Success);
        Assert.Contains("path", empty.Error);
        var missing = await tool.ExecuteAsync(Call("{\"path\":\"nope.png\"}"), Ctx());
        Assert.False(missing.Success);
        Assert.Contains("文件不存在", missing.Error);
    }
}
