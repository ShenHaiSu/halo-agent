using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// need09-05：会话图片附件存储（引用落 messages.jsonl，字节落 attachments/；sha16 去重；只存引用不存 base64）。
/// 由 SessionRepository / ViewImageTool / Bridge 共享；Orchestrator 不直接依赖。
/// </summary>
public static class ImageAttachmentStore
{
    public const long MaxImageBytes = 8L * 1024 * 1024;
    public const int NormalizeMaxEdge = 2048;
    public const int ThumbnailMaxEdge = 256;
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly object _writeLock = new();

    /// <summary>need09-05：支持的 mime（jpg/png/webp）。</summary>
    public static bool IsSupportedExtension(string? fileName)
    {
        string ext = (
            Path.GetExtension(fileName ?? string.Empty) ?? string.Empty
        ).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp";
    }

    /// <summary>need09-05：按扩展名推断 mime。</summary>
    public static string MimeFromExtension(string? fileName)
    {
        string ext = (
            Path.GetExtension(fileName ?? string.Empty) ?? string.Empty
        ).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
    }

    /// <summary>need09-05：魔数校验（FFD8/PNG89/RIFF-WEBP）。</summary>
    public static bool CheckMagic(byte[] bytes, string mime)
    {
        if (bytes == null || bytes.Length < 12)
            return false;
        if (mime == "image/png")
            return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        if (mime == "image/jpeg")
            return bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        if (mime == "image/webp")
            return bytes[0] == (byte)'R'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W'
                && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B'
                && bytes[11] == (byte)'P';
        return false;
    }

    /// <summary>need09-05：SHA256 hex 前 16 位（去重键）。</summary>
    public static string ComputeSha16(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>need09-05：文件名清洗（非法字符→_，限长 64，空回退 image）。</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "image";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Trim().Length);
        foreach (char c in name.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string r = sb.ToString().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(r))
            r = "image";
        return r.Length > 64 ? r.Substring(0, 64) : r;
    }

    public static string GetAttachmentsDir(string dataRoot, string workspaceId, string sessionId) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
            "sessions",
            workspaceId,
            sessionId,
            "attachments"
        );

    /// <summary>need09-05：按 imageId 扫描附件目录（跨日期去重命中即复用）。</summary>
    public static string? FindImagePath(
        string dataRoot,
        string workspaceId,
        string sessionId,
        string imageId
    )
    {
        try
        {
            string dir = GetAttachmentsDir(dataRoot, workspaceId, sessionId);
            if (!Directory.Exists(dir))
                return null;
            foreach (string dateDir in Directory.GetDirectories(dir))
            {
                string[] hits = Directory.GetFiles(dateDir, imageId + "_*");
                if (hits.Length > 0)
                    return hits.OrderBy(h => h, StringComparer.Ordinal).First();
            }
        }
        catch
        { /* 查找永不抛 */
        }
        return null;
    }

    public static string? ResolveDataUrl(
        string dataRoot,
        string workspaceId,
        string sessionId,
        ChatImageRef img
    )
    {
        try
        {
            if (img == null || string.IsNullOrWhiteSpace(img.ImageId))
                return null;
            string? abs = FindImagePath(dataRoot, workspaceId, sessionId, img.ImageId.Trim());
            if (abs == null || !File.Exists(abs))
                return null;
            byte[] bytes = File.ReadAllBytes(abs);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                return null;
            string mime = MimeFromExtension(abs);
            if (!CheckMagic(bytes, mime))
                return null;
            return ToDataUrl(bytes, mime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>need09-05：保存图片附件（sha16 去重；返回引用，IsNew 标识是否新落盘）。</summary>
    public static (ChatImageRef Ref, bool IsNew) SaveImage(
        string dataRoot,
        string workspaceId,
        string sessionId,
        string fileName,
        string mime,
        byte[] bytes,
        int width,
        int height
    )
    {
        string sha16 = ComputeSha16(bytes);
        string imageId = "img_" + sha16;
        lock (_writeLock)
        {
            string? existing = FindImagePath(dataRoot, workspaceId, sessionId, imageId);
            if (existing != null)
            {
                var fi = new FileInfo(existing);
                string rel = Path.GetRelativePath(
                        Path.Combine(
                            string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
                            "sessions",
                            workspaceId,
                            sessionId
                        ),
                        existing
                    )
                    .Replace(Path.DirectorySeparatorChar, '/');
                return (
                    new ChatImageRef
                    {
                        ImageId = imageId,
                        FileName = Path.GetFileName(existing).Substring(imageId.Length + 1),
                        Mime = MimeFromExtension(existing),
                        RelPath = rel,
                        Width = width,
                        Height = height,
                        SizeBytes = fi.Length,
                        Sha16 = sha16
                    },
                    false
                );
            }
            string safe = SanitizeFileName(fileName);
            string date = DateTime.UtcNow.ToString("yyyyMMdd");
            string dir = Path.Combine(GetAttachmentsDir(dataRoot, workspaceId, sessionId), date);
            Directory.CreateDirectory(dir);
            string abs = Path.Combine(dir, imageId + "_" + safe);
            File.WriteAllBytes(abs, bytes);
            string sessionDir = Path.Combine(
                string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot,
                "sessions",
                workspaceId,
                sessionId
            );
            string relPath = Path.GetRelativePath(sessionDir, abs)
                .Replace(Path.DirectorySeparatorChar, '/');
            return (
                new ChatImageRef
                {
                    ImageId = imageId,
                    FileName = safe,
                    Mime = mime,
                    RelPath = relPath,
                    Width = width,
                    Height = height,
                    SizeBytes = bytes.LongLength,
                    Sha16 = sha16
                },
                true
            );
        }
    }

    /// <summary>need09-05：解析图片宽高（PNG-IHDR / JPEG-SOF / WEBP-VP8 系；失败返回 0,0）。</summary>
    public static (int Width, int Height) TryParseDimensions(byte[] bytes, string mime)
    {
        try
        {
            if (bytes == null || bytes.Length < 24)
                return (0, 0);
            if (mime == "image/png" && bytes.Length >= 24)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w > 0 && w < 100000 && h > 0 && h < 100000) ? (w, h) : (0, 0);
            }
            if (mime == "image/jpeg")
            {
                int i = 2;
                while (i + 8 < bytes.Length)
                {
                    if (bytes[i] != 0xFF)
                        break;
                    byte marker = bytes[i + 1];
                    if (marker == 0xD8 || marker == 0xD9)
                    {
                        i += 2;
                        continue;
                    }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        i += 2;
                        continue;
                    }
                    int len = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (len < 2)
                        break;
                    bool isSof = (
                        marker >= 0xC0
                        && marker <= 0xCF
                        && marker != 0xC4
                        && marker != 0xC8
                        && marker != 0xCC
                    );
                    if (isSof && i + 8 < bytes.Length)
                    {
                        int h = (bytes[i + 5] << 8) | bytes[i + 6];
                        int w = (bytes[i + 7] << 8) | bytes[i + 8];
                        return (w > 0 && h > 0) ? (w, h) : (0, 0);
                    }
                    i += 2 + len;
                }
                return (0, 0);
            }
            if (mime == "image/webp" && bytes.Length >= 30)
            {
                string chunk = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
                if (chunk == "VP8X" && bytes.Length >= 30)
                {
                    int w = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
                    int h = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
                    return (w > 0 && h > 0) ? (w, h) : (0, 0);
                }
                if (chunk == "VP8 " && bytes.Length >= 30)
                {
                    int w = bytes[26] | (bytes[27] << 8);
                    int h = bytes[28] | (bytes[29] << 8);
                    w &= 0x3FFF;
                    h &= 0x3FFF;
                    return (w > 0 && h > 0) ? (w, h) : (0, 0);
                }
                if (chunk == "VP8L" && bytes.Length >= 25)
                {
                    int b0 = bytes[21],
                        b1 = bytes[22],
                        b2 = bytes[23],
                        b3 = bytes[24];
                    int w = ((b1 & 0x3F) << 8 | b0) + 1;
                    int h = ((b3 & 0x0F) << 10 | (b2 << 2) | ((b1 & 0xC0) >> 6)) + 1;
                    return (w > 0 && h > 0 && w < 100000 && h < 100000) ? (w, h) : (0, 0);
                }
            }
        }
        catch
        { /* 解析永不抛 */
        }
        return (0, 0);
    }

    /// <summary>need09-fix：无损入库（tool 读图与贴图统一入口）：原字节直存，仅解析宽高作元数据，永不缩放/转码。
    /// detail 只影响发往模型的清晰度参数，不影响落盘字节；同图重读 sha 一致，直接命中去重缓存。</summary>
    public static (byte[] Bytes, string Mime, int Width, int Height) PrepareLossless(
        byte[] bytes,
        string mime
    )
    {
        var (w, h) = TryParseDimensions(bytes, mime);
        if (w <= 0 || h <= 0)
        {
            try
            {
                using var probe = System.Drawing.Image.FromStream(
                    new MemoryStream(bytes, writable: false)
                );
                w = probe.Width;
                h = probe.Height;
            }
            catch
            { /* 维度解析失败保留 0,0，不阻断入库 */
            }
        }
        return (bytes, mime, w, h);
    }

    /// <summary>need09-05：归一化（长边≤maxEdge 才原样；超限则 System.Drawing 等比缩放转 JPEG q85；失败回退原图）。
    /// need09-fix：已停用——tool/贴图入库统一走 PrepareLossless 无损直存，避免历史降质与缓存 key 失配。本方法仅保留兼容历史附件读取。</summary>
    public static (byte[] Bytes, string Mime, int Width, int Height) NormalizeImage(
        byte[] bytes,
        string mime,
        int maxEdge = NormalizeMaxEdge
    )
    {
        var (w, h) = TryParseDimensions(bytes, mime);
        try
        {
            if (w <= 0 || h <= 0)
            {
                using var probe = System.Drawing.Image.FromStream(
                    new MemoryStream(bytes, writable: false)
                );
                w = probe.Width;
                h = probe.Height;
            }
            if (w > 0 && h > 0 && System.Math.Max(w, h) <= maxEdge)
                return (bytes, mime, w, h);
            using var src = System.Drawing.Image.FromStream(
                new MemoryStream(bytes, writable: false)
            );
            double scale = (double)maxEdge / System.Math.Max(src.Width, src.Height);
            int nw = System.Math.Max(1, (int)(src.Width * scale));
            int nh = System.Math.Max(1, (int)(src.Height * scale));
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            var codec =
                System
                    .Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                ?? System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()[0];
            using var ms = new MemoryStream();
            using (var ep = new System.Drawing.Imaging.EncoderParameters(1))
            {
                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality,
                    85L
                );
                bmp.Save(ms, codec, ep);
            }
            return (ms.ToArray(), "image/jpeg", nw, nh);
        }
        catch
        {
            return (bytes, mime, w, h);
        }
    }

    /// <summary>need09-05：缩略图 dataURL（长边≤256 PNG；失败返回 null）。</summary>
    public static string? MakeThumbnailDataUrl(byte[] bytes, int maxEdge = ThumbnailMaxEdge)
    {
        try
        {
            using var src = System.Drawing.Image.FromStream(
                new MemoryStream(bytes, writable: false)
            );
            double scale = (double)maxEdge / System.Math.Max(src.Width, src.Height);
            int nw = src.Width,
                nh = src.Height;
            if (scale < 1.0)
            {
                nw = System.Math.Max(1, (int)(src.Width * scale));
                nh = System.Math.Max(1, (int)(src.Height * scale));
            }
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static string ToDataUrl(byte[] bytes, string mime) =>
        "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
}
