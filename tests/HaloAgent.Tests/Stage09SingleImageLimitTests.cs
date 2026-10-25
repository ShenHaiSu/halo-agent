#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S4 §5 验收 1~2：单图上限统一十进制 10M（raw）+ 常量单源。
/// 数值锁定防手滑；边界行为用真文件（CheckMagic 需合法文件头 → 用合法小 png padding 到目标尺寸）。
/// </summary>
public sealed class Stage09SingleImageLimitTests : IDisposable
{
    private const string Png1x1Base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public Stage09SingleImageLimitTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentS4_{Guid.NewGuid():N}");
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
            WorkspaceId = "ws_s4",
            SessionId = "sess_s4",
            DataRoot = _dataRoot,
        };

    private static ToolCallDto Call(string argsJson) => new("call_s4", "view_image", argsJson);

    /// <summary>合法小 png 按目标尺寸 padding（文件头合法，CheckMagic 可过）。</summary>
    private static byte[] PaddedPng(long targetBytes)
    {
        byte[] png = Convert.FromBase64String(Png1x1Base64);
        if (targetBytes < png.Length)
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        byte[] blob = new byte[targetBytes];
        Buffer.BlockCopy(png, 0, blob, 0, png.Length);
        return blob;
    }

    // 验收 1：数值锁定（十进制 10M，防手滑改回 8MiB）。
    [Fact(DisplayName = "S4-1: MaxImageBytes == 10_000_000（十进制 raw，对齐 provider 10MB）")]
    public void MaxImageBytes_IsTenMillion()
    {
        Assert.Equal(10_000_000L, ImageAttachmentStore.MaxImageBytes);
    }

    // 验收 2a：9MB 图可入库可发送（SaveImage + view_image 成功）。
    [Fact(DisplayName = "S4-2: 9MB 图可入库（SaveImage 成功，SizeBytes 为 raw）")]
    public void NineMegabytes_SaveImage_Succeeds()
    {
        const long nineMB = 9_000_000L;
        byte[] blob = PaddedPng(nineMB);
        var (imgRef, isNew) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            "ws_s4",
            "sess_s4",
            "nine.png",
            "image/png",
            blob,
            1,
            1
        );
        Assert.True(isNew);
        Assert.Equal(nineMB, imgRef.SizeBytes);

        // ResolveDataUrl 同样可读回（S2 有界读按新上限放行）。
        string? url = ImageAttachmentStore.ResolveDataUrl(_dataRoot, "ws_s4", "sess_s4", imgRef);
        Assert.NotNull(url);
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(
            blob.Length,
            Convert.FromBase64String(url.Substring("data:image/png;base64,".Length)).Length
        );
    }

    [Fact(DisplayName = "S4-3: 9MB 图 view_image 成功（无 IMAGE_TOO_LARGE）")]
    public async Task NineMegabytes_ViewImage_Succeeds()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "nine.png"), PaddedPng(9_000_000L));
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"nine.png\"}"),
            Ctx()
        );
        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
        Assert.Single(result.Images!);
    }

    // 验收 2b：10M+1 字节被拒（IMAGE_TOO_LARGE，payload limit 取常量）。
    [Fact(DisplayName = "S4-4: 10M+1 字节被拒（IMAGE_TOO_LARGE，limit 取常量）")]
    public async Task OverLimit_ViewImage_Rejects()
    {
        long over = ImageAttachmentStore.MaxImageBytes + 1;
        File.WriteAllBytes(Path.Combine(_workspace, "over.png"), PaddedPng(over));
        var result = await new ViewImageTool().ExecuteAsync(
            Call("{\"file_path\":\"over.png\"}"),
            Ctx()
        );
        Assert.False(result.Success);
        Assert.Equal(ViewImageTool.ErrorCodes.ImageTooLarge, result.ErrorCode);
        Assert.Contains("上限 10MB", result.Error);
        Assert.NotNull(result.ErrorPayloadJson);
        Assert.Contains("\"limit\":" + ImageAttachmentStore.MaxImageBytes, result.ErrorPayloadJson);
        Assert.Contains("\"actual\":" + over, result.ErrorPayloadJson);
    }
}
