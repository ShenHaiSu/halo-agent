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

/// <summary>S2 有界读回归（take(limit+1) 语义，真文件放 Path.GetTempPath，跑完删）。</summary>
public sealed class Stage09BoundedReadTests : IDisposable
{
    private const string Png1x1Base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public Stage09BoundedReadTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09Bounded_{Guid.NewGuid():N}");
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
            WorkspaceId = "ws_test",
            SessionId = "sess_test",
            DataRoot = _dataRoot,
        };

    private static ToolCallDto Call(string argsJson) => new("call_1", "view_image", argsJson);

    [Fact(DisplayName = "S2-1: TryReadBounded 越限返回 false（MaxImageBytes+1，不抛）")]
    public void TryReadBounded_OverLimit_ReturnsFalse()
    {
        string path = Path.Combine(_workspace, "over.bin");
        byte[] blob = new byte[ImageAttachmentStore.MaxImageBytes + 1];
        new Random(42).NextBytes(blob);
        File.WriteAllBytes(path, blob);
        Assert.False(
            ImageAttachmentStore.TryReadBounded(
                path,
                ImageAttachmentStore.MaxImageBytes,
                out byte[]? bytes
            )
        );
        Assert.Null(bytes);
        // 上限内正常读回。
        string okPath = Path.Combine(_workspace, "ok.bin");
        File.WriteAllBytes(okPath, new byte[] { 1, 2, 3 });
        Assert.True(
            ImageAttachmentStore.TryReadBounded(
                okPath,
                ImageAttachmentStore.MaxImageBytes,
                out byte[]? ok
            )
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, ok);
    }

    [Fact(DisplayName = "S2-2: TOCTOU 语义 limit=10 对 11 字节返回 false 且不抛")]
    public void TryReadBounded_TouchBoundary_ReturnsFalse()
    {
        string path = Path.Combine(_workspace, "edge.bin");
        File.WriteAllBytes(path, new byte[11]);
        Assert.False(ImageAttachmentStore.TryReadBounded(path, 10, out byte[]? bytes));
        Assert.Null(bytes);
        File.WriteAllBytes(path, new byte[10]);
        Assert.True(ImageAttachmentStore.TryReadBounded(path, 10, out byte[]? ok));
        Assert.Equal(10, ok!.Length);
    }

    [Fact(DisplayName = "S2-3: 空文件返回 false")]
    public void TryReadBounded_Empty_ReturnsFalse()
    {
        string path = Path.Combine(_workspace, "empty.bin");
        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.False(ImageAttachmentStore.TryReadBounded(path, 10, out byte[]? bytes));
        Assert.Null(bytes);
    }

    [Fact(DisplayName = "S2-4: 超限图 ViewImageTool 失败 + ResolveDataUrl 返回 null")]
    public async Task OverLimit_ViewImageFails_ResolveDataUrlNull()
    {
        byte[] png = Convert.FromBase64String(Png1x1Base64);
        byte[] blob = new byte[ImageAttachmentStore.MaxImageBytes + 1];
        Buffer.BlockCopy(png, 0, blob, 0, png.Length);
        string name = "over.png";
        File.WriteAllBytes(Path.Combine(_workspace, name), blob);

        var tool = new ViewImageTool();
        var result = await tool.ExecuteAsync(Call("{\"path\":\"over.png\"}"), Ctx());
        Assert.False(result.Success);
        Assert.Contains("图片超限", result.Error);

        // ResolveDataUrl 同样拒：手动落一个超限附件再读。
        var (imgRef, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            "ws_test",
            "sess_test",
            "tiny.png",
            "image/png",
            png,
            1,
            1
        );
        // 把附件文件撑大到超限，模拟 TOCTOU / 磁盘替换。
        string? abs = ImageAttachmentStore.FindImagePath(
            _dataRoot,
            "ws_test",
            "sess_test",
            imgRef.ImageId
        );
        Assert.NotNull(abs);
        File.WriteAllBytes(abs!, blob);
        Assert.Null(ImageAttachmentStore.ResolveDataUrl(_dataRoot, "ws_test", "sess_test", imgRef));
    }

    [Fact(DisplayName = "S2-5: base64 前筛超限直接拒（不解码大包）")]
    public void TryDecodeBase64Bounded_OverLimit_ReturnsFalse()
    {
        // limit=3 时 B64 上限 = 3*4/3+4 = 8；给 16 个合法 b64 字符应被前筛拒。
        Assert.False(
            ImageAttachmentStore.TryDecodeBase64Bounded(new string('A', 16), 3, out byte[]? bytes)
        );
        Assert.Null(bytes);
        // 小包正常。
        Assert.True(
            ImageAttachmentStore.TryDecodeBase64Bounded(
                Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                ImageAttachmentStore.MaxImageBytes,
                out byte[]? ok
            )
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, ok);
        // 非法 base64 返回 false 不抛。
        Assert.False(
            ImageAttachmentStore.TryDecodeBase64Bounded(
                "!!!not-base64!!!",
                ImageAttachmentStore.MaxImageBytes,
                out _
            )
        );
    }

    [Fact(DisplayName = "S2-6: 正常图链路不变（读写 + ResolveDataUrl 回环）")]
    public void NormalImage_RoundTrip_Ok()
    {
        byte[] png = Convert.FromBase64String(Png1x1Base64);
        var (imgRef, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            "ws_test",
            "sess_test",
            "a.png",
            "image/png",
            png,
            1,
            1
        );
        string? url = ImageAttachmentStore.ResolveDataUrl(
            _dataRoot,
            "ws_test",
            "sess_test",
            imgRef
        );
        Assert.NotNull(url);
        Assert.StartsWith("data:image/png;base64,", url);
    }
}
