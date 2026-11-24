using System;
using System.IO;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Bridge;

/// <summary>
/// 附件解析拆分的 stored 分支：只接受当前工作区与会话内已存在的图片引用。
/// 客户端相对路径一律忽略，非法 ID、缺图与损坏文件只产生警告，不阻断文字发送。
/// </summary>
internal static class TurnAttachmentIngestor
{
    public static bool TryStored(
        StoragePathManager storage,
        string workspaceId,
        string sessionId,
        JsonElement element,
        int index,
        out ChatImageRef? reference,
        out long sizeBytes
    )
    {
        reference = null;
        sizeBytes = 0;
        string imageId =
            element.TryGetProperty("imageId", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
                ? (idElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
        if (!ImageAttachmentStore.IsSafeImageId(imageId))
        {
            WarnReject(workspaceId, sessionId, imageId, "invalid-id");
            return false;
        }
        string fileName =
            element.TryGetProperty("fileName", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? (nameElement.GetString() ?? $"stored-{index}.png")
                : $"stored-{index}.png";
        string mime =
            element.TryGetProperty("mime", out var mimeElement)
            && mimeElement.ValueKind == JsonValueKind.String
                ? (mimeElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                : ImageAttachmentStore.MimeFromExtension(fileName);
        if (string.IsNullOrWhiteSpace(mime))
            mime = ImageAttachmentStore.MimeFromExtension(fileName);

        string? sourcePath = ImageAttachmentStore.FindImagePath(
            storage.CurrentDataRoot,
            workspaceId,
            sessionId,
            imageId
        );
        if (
            string.IsNullOrWhiteSpace(sourcePath)
            || !File.Exists(sourcePath)
            || !ImageAttachmentStore.TryReadBounded(
                sourcePath,
                ImageAttachmentStore.MaxImageBytes,
                out var raw
            )
            || raw == null
            || !ImageAttachmentStore.CheckMagic(raw, mime)
        )
        {
            WarnReject(workspaceId, sessionId, imageId, "missing-or-corrupt");
            return false;
        }
        var (bytes, normalizedMime, width, height) = ImageAttachmentStore.PrepareLossless(
            raw,
            mime
        );
        string attachmentsRoot = ImageAttachmentStore.GetAttachmentsDir(
            storage.CurrentDataRoot,
            workspaceId,
            sessionId
        );
        string relPath = sourcePath.StartsWith(
            attachmentsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        )
            ? sourcePath.Substring(attachmentsRoot.Length + 1)
            : Path.GetFileName(sourcePath);
        reference = new ChatImageRef
        {
            ImageId = imageId,
            FileName = ImageAttachmentStore.SanitizeFileName(fileName),
            Mime = normalizedMime,
            RelPath = relPath.Replace('\\', '/'),
            Width = width,
            Height = height,
            SizeBytes = bytes.LongLength,
            Sha16 = ImageAttachmentStore.ComputeSha16(raw),
        };
        sizeBytes = bytes.LongLength;
        return true;
    }

    private static void WarnReject(
        string workspaceId,
        string sessionId,
        string imageId,
        string reason
    )
    {
        try
        {
            HaloLogger.App.Warn(
                "[History] image.reuse.miss ws="
                    + workspaceId
                    + " sess="
                    + sessionId
                    + " image="
                    + imageId
                    + " reason="
                    + reason
            );
        }
        catch { }
    }
}
