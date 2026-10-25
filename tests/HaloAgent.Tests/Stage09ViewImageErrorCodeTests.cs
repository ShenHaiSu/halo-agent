#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S3 §5 验收 1~2：view_image 四态错误码机器可判
/// （IMAGE_TOO_LARGE / NOT_AN_IMAGE / IMAGE_MISSING / PATH_TRAVERSAL），
/// 且 Error 首 token 为 `CODE：` 形态（TryExtractCode 可提取）。
/// </summary>
public sealed class Stage09ViewImageErrorCodeTests : IDisposable
{
    private const string Png1x1Base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public Stage09ViewImageErrorCodeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentS3Code_{Guid.NewGuid():N}");
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
        catch { }
    }

    private ToolContext Ctx() =>
        new()
        {
            WorkspaceRoot = _workspace,
            WorkspaceId = "ws_s3",
            SessionId = "sess_s3",
            DataRoot = _dataRoot,
        };

    private static ToolCallDto Call(string argsJson) => new("call_s3", "view_image", argsJson);

    // 验收 1 + 2：四态各自精确命中 ErrorCode，且 Error 首 token 可提取。
    private static void AssertCode(ToolResult result, string expected)
    {
        Assert.False(result.Success);
        Assert.Equal(expected, result.ErrorCode);
        // Error 首 token 必为 CODE：形态（TryExtractCode 可提）。
        Assert.Equal(expected, ToolResult.TryExtractCode(result.Error));
        Assert.StartsWith(expected + "：", result.Error, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "S3-1: 超限 → IMAGE_TOO_LARGE（payload 含 limit/actual）")]
    public async Task OverLimit_ImageTooLarge()
    {
        byte[] png = Convert.FromBase64String(Png1x1Base64);
        byte[] blob = new byte[ImageAttachmentStore.MaxImageBytes + 1];
        Buffer.BlockCopy(png, 0, blob, 0, png.Length);
        File.WriteAllBytes(Path.Combine(_workspace, "over.png"), blob);

        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"over.png\"}"),
            Ctx()
        );
        AssertCode(result, ViewImageTool.ErrorCodes.ImageTooLarge);
        Assert.NotNull(result.ErrorPayloadJson);
        Assert.Contains("\"limit\":" + ImageAttachmentStore.MaxImageBytes, result.ErrorPayloadJson);
        Assert.Contains("\"actual\":" + blob.LongLength, result.ErrorPayloadJson);
    }

    [Fact(DisplayName = "S3-2: 扩展名不支持（gif）→ NOT_AN_IMAGE")]
    public async Task UnsupportedExtension_NotAnImage()
    {
        byte[] gif =
        {
            (byte)'G',
            (byte)'I',
            (byte)'F',
            (byte)'8',
            (byte)'9',
            (byte)'a',
            1,
            0,
            1,
            0,
            0,
            0,
            0,
        };
        File.WriteAllBytes(Path.Combine(_workspace, "a.gif"), gif);

        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"a.gif\"}"),
            Ctx()
        );
        AssertCode(result, ViewImageTool.ErrorCodes.NotAnImage);
        // S7-E4：拒绝文案统一为 ViewImageTool.UnsupportedTypeReason（GIF 不进视觉、SVG 指路 read_file）。
        Assert.Contains(ViewImageTool.UnsupportedTypeReason, result.Error);
        Assert.Contains("read_file", result.Error);
    }

    [Fact(DisplayName = "S3-3: 魔数不符（伪装 png）→ NOT_AN_IMAGE")]
    public async Task MagicMismatch_NotAnImage()
    {
        File.WriteAllText(Path.Combine(_workspace, "fake.png"), "this is definitely not an image");
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"fake.png\"}"),
            Ctx()
        );
        AssertCode(result, ViewImageTool.ErrorCodes.NotAnImage);
        Assert.Contains("文件头校验失败", result.Error);
    }

    [Fact(DisplayName = "S3-4: 文件不存在 → IMAGE_MISSING")]
    public async Task MissingFile_ImageMissing()
    {
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"nope.png\"}"),
            Ctx()
        );
        AssertCode(result, ViewImageTool.ErrorCodes.ImageMissing);
    }

    [Fact(DisplayName = "S3-5: ../ 遍历 → PATH_TRAVERSAL")]
    public async Task Traversal_PathTraversal()
    {
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"../secret.png\"}"),
            Ctx()
        );
        AssertCode(result, ViewImageTool.ErrorCodes.PathTraversal);
    }

    [Fact(DisplayName = "S3-6: 系统目录 / 索引文件 → PATH_TRAVERSAL")]
    public async Task SystemPathAndIndexFile_PathTraversal()
    {
        var tool = new ViewImageTool();
        var sys = await tool.ExecuteAsync(
            Call("{\"file_path\":\"C:\\\\Windows\\\\System32\\\\x.png\"}"),
            Ctx()
        );
        AssertCode(sys, ViewImageTool.ErrorCodes.PathTraversal);

        var idx = await tool.ExecuteAsync(
            Call("{\"file_path\":\".haloagent/skills/_index.json\"}"),
            Ctx()
        );
        AssertCode(idx, ViewImageTool.ErrorCodes.PathTraversal);
    }

    [Fact(DisplayName = "S3-7: Description 声明四态错误码（模型可读）")]
    public void Description_ListsErrorCodes()
    {
        var def = new ViewImageTool().GetDefinition();
        Assert.Contains("IMAGE_TOO_LARGE", def.Description);
        Assert.Contains("NOT_AN_IMAGE", def.Description);
        Assert.Contains("IMAGE_MISSING", def.Description);
        Assert.Contains("PATH_TRAVERSAL", def.Description);
    }

    [Fact(DisplayName = "S3-8: 正常小图仍成功且无 ErrorCode（回归不误伤）")]
    public async Task ValidImage_StillSucceeds()
    {
        File.WriteAllBytes(
            Path.Combine(_workspace, "ok.png"),
            Convert.FromBase64String(Png1x1Base64)
        );
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"ok.png\"}"),
            Ctx()
        );
        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
        Assert.Single(result.Images!);
    }

    [Fact(DisplayName = "S3-9: 四态错误码各自 Hint 可下发（模型可自愈）")]
    public void ErrorCodes_HaveHints()
    {
        foreach (
            string code in new[]
            {
                ViewImageTool.ErrorCodes.ImageTooLarge,
                ViewImageTool.ErrorCodes.NotAnImage,
                ViewImageTool.ErrorCodes.ImageMissing,
                ViewImageTool.ErrorCodes.PathTraversal,
            }
        )
        {
            string hint = Host.Desktop.Services.AgentOrchestrator.HintForTool("view_image", code);
            Assert.Contains(" Hint: ", hint);
        }
    }
}
