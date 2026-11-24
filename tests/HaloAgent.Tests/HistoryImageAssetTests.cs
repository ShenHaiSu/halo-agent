#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>历史图片资产：分支文件复制与 stored 复用的会话/工作区隔离。</summary>
public sealed class HistoryImageAssetTests : IDisposable
{
    private readonly string _root;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;

    public HistoryImageAssetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"HistoryImageTests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HistoryImageBootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _root,
            customFallbackDataRoot: _root
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言
        }
    }

    private static byte[] PngBytes()
    {
        var bytes = new byte[16];
        bytes[0] = 0x89;
        bytes[1] = (byte)'P';
        bytes[2] = (byte)'N';
        bytes[3] = (byte)'G';
        return bytes;
    }

    private ChatImageRef SaveSourceImage(string workspaceId, string sessionId)
    {
        var (reference, _) = ImageAttachmentStore.SaveImage(
            _root,
            workspaceId,
            sessionId,
            "source.png",
            "image/png",
            PngBytes(),
            1,
            1
        );
        return reference;
    }

    [Fact]
    public void BranchCopyUsesFileCopyAndSurvivesSourceDeletion()
    {
        var source = SaveSourceImage("ws", "src");
        var (copied, missing) = ImageAttachmentStore.CopyToSession(
            _root,
            "ws",
            "src",
            "ws",
            "branch",
            new[] { source }
        );
        Assert.Equal(1, copied);
        Assert.Equal(0, missing);

        string? sourcePath = ImageAttachmentStore.FindImagePath(_root, "ws", "src", source.ImageId);
        Assert.NotNull(sourcePath);
        File.Delete(sourcePath!);

        string? branchPath = ImageAttachmentStore.FindImagePath(
            _root,
            "ws",
            "branch",
            source.ImageId
        );
        Assert.NotNull(branchPath);
        Assert.True(File.Exists(branchPath!));
    }

    [Fact]
    public void BranchCopyRejectsUnsafeIdsAndCrossSessionReferences()
    {
        var source = SaveSourceImage("ws", "src");
        var unsafeImage = new ChatImageRef
        {
            ImageId = "../escape",
            FileName = "x.png",
            Mime = "image/png"
        };
        var (copied, missing) = ImageAttachmentStore.CopyToSession(
            _root,
            "ws",
            "src",
            "ws",
            "branch",
            new[] { source, unsafeImage }
        );
        Assert.Equal(1, copied);
        Assert.Equal(1, missing);

        var crossSession = ImageAttachmentStore.CopyToSession(
            _root,
            "ws",
            "other-session",
            "ws",
            "branch2",
            new[] { source }
        );
        Assert.Equal(0, crossSession.Copied);
        Assert.Equal(1, crossSession.Missing);
    }

    private static JsonElement StoredElement(string imageId, string workspaceHint)
    {
        using var document = JsonDocument.Parse(
            "{\"kind\":\"stored\",\"imageId\":\""
                + imageId
                + "\","
                + "\"fileName\":\"source.png\",\"mime\":\"image/png\","
                + "\"relPath\":\""
                + workspaceHint
                + "\"}"
        );
        return document.RootElement.Clone();
    }

    [Fact]
    public void StoredReuseOnlyAcceptsCurrentSessionAndWorkspace()
    {
        var source = SaveSourceImage("ws", "src");
        var element = StoredElement(source.ImageId, "../forged/path");

        Assert.True(
            TurnAttachmentIngestor.TryStored(
                _storage,
                "ws",
                "src",
                element,
                0,
                out var accepted,
                out var bytes
            )
        );
        Assert.NotNull(accepted);
        Assert.Equal(source.ImageId, accepted!.ImageId);
        Assert.True(bytes > 0);
        Assert.DoesNotContain("..", accepted.RelPath);

        Assert.False(
            TurnAttachmentIngestor.TryStored(_storage, "ws", "another", element, 0, out _, out _)
        );
        Assert.False(
            TurnAttachmentIngestor.TryStored(
                _storage,
                "another-ws",
                "src",
                element,
                0,
                out _,
                out _
            )
        );
        Assert.False(
            TurnAttachmentIngestor.TryStored(
                _storage,
                "ws",
                "src",
                StoredElement("../escape", ""),
                0,
                out _,
                out _
            )
        );
    }
}
