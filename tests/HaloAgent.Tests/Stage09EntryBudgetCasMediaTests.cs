#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S7（07-stage7-entry-cas-media）后端验收：E2 全局内容寻址 / 并发独占创建 / E3 TTL 清理口径 / E4 类型决策。
///
/// 并行策略：本类**不触碰任何进程级静态可变状态**（不用 HaloLogger.Initialize、不改 TokenBudget 常量、
/// 不改 BackupStore 配额），每个用例自带 <see cref="IDisposable"/> 临时数据根，故走 xunit 默认并行集合，
/// 与其它类并行执行，不必进 Serial-* 串行集合（避免拖慢全量）。
///
/// E1 的后端三段式复核依赖 HostBridgeFacade 的实例状态（StorageManager），
/// 其口径已由前端纯函数单测（composerModel.entryBudget.spec.ts）与常量锁定覆盖，此处只锁常量同值。
/// </summary>
public sealed class Stage09EntryBudgetCasMediaTests : IDisposable
{
    private const string Png1x1Base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _tempRoot;
    private readonly string _dataRoot;

    public Stage09EntryBudgetCasMediaTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentS7_{Guid.NewGuid():N}");
        _dataRoot = Path.Combine(_tempRoot, "data");
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

    /// <summary>合法小 png，尾部追加区分字节（同尺寸不同内容 → sha16 不同）。</summary>
    private static byte[] Png(byte marker = 0, int padTo = 0)
    {
        byte[] png = Convert.FromBase64String(Png1x1Base64);
        int size = padTo > png.Length ? padTo : png.Length + 1;
        byte[] blob = new byte[size];
        Buffer.BlockCopy(png, 0, blob, 0, png.Length);
        blob[blob.Length - 1] = marker;
        return blob;
    }

    private const string Ws = "ws_s7";
    private const string SessA = "sess_a";
    private const string SessB = "sess_b";

    // ===== E1：常量同值锁定（与前端 composerModel 镜像；改了这里必须同步改前端与文档） =====

    [Fact(DisplayName = "S7-E1: 后端入口预算常量与前端同值（20 张 / 128MB 十进制 / 单体 10MB）")]
    public void E1_Constants_MirrorFrontend()
    {
        var t = typeof(Host.Desktop.Bridge.HostBridgeFacade);
        var fMax = t.GetField(
            "S7MaxFilesPerTurn",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        var tMax = t.GetField(
            "S7MaxTotalBytes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(fMax);
        Assert.NotNull(tMax);
        Assert.Equal(20, (int)fMax!.GetValue(null)!);
        Assert.Equal(128_000_000L, (long)tMax!.GetValue(null)!);
        Assert.Equal(10_000_000L, ImageAttachmentStore.MaxImageBytes);
        // 三段式必须自洽：总量盘 ≥ 单体盘（否则单体永远先于总量命中）。
        Assert.True(128_000_000L > ImageAttachmentStore.MaxImageBytes);
    }

    // ===== E2：全局内容寻址 =====

    [Fact(DisplayName = "S7-E2-1: 同字节跨会话只落一份全局 blob（两个 sessionId 各存一次）")]
    public void E2_1_SameBytes_AcrossSessions_SingleGlobalBlob()
    {
        byte[] bytes = Png(1);
        var (refA, newA) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "a.png",
            "image/png",
            bytes,
            1,
            1
        );
        var (refB, newB) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessB,
            "b.png",
            "image/png",
            bytes,
            1,
            1
        );

        // 会话级各自新落盘（各会话仍持自己的引用文件，TTL/会话删除互不影响）
        Assert.True(newA);
        Assert.True(newB);
        Assert.Equal(refA.ImageId, refB.ImageId); // img_<sha16> 跨会话一致（内容寻址键）

        // 全局 blob 只有一份
        string globalDir = ImageAttachmentStore.GetGlobalAttachmentsDir(_dataRoot);
        Assert.True(Directory.Exists(globalDir));
        string[] all = Directory.GetFiles(globalDir, "*", SearchOption.AllDirectories);
        Assert.Single(all);
        Assert.Equal(refA.Sha16, ImageAttachmentStore.ComputeSha16(bytes));
        Assert.StartsWith(refA.Sha16 + "_", Path.GetFileName(all[0]));

        // 会话文件仍可读回（读取主路径不依赖全局 blob）
        Assert.NotNull(ImageAttachmentStore.ResolveDataUrl(_dataRoot, Ws, SessA, refA));
        Assert.NotNull(ImageAttachmentStore.ResolveDataUrl(_dataRoot, Ws, SessB, refB));
    }

    [Fact(DisplayName = "S7-E2-1b: 不同字节各落一份全局 blob（不误合并）")]
    public void E2_1b_DifferentBytes_SeparateBlobs()
    {
        ImageAttachmentStore.SaveImage(_dataRoot, Ws, SessA, "x1.png", "image/png", Png(1), 1, 1);
        ImageAttachmentStore.SaveImage(_dataRoot, Ws, SessA, "x2.png", "image/png", Png(2), 1, 1);
        string[] all = Directory.GetFiles(
            ImageAttachmentStore.GetGlobalAttachmentsDir(_dataRoot),
            "*",
            SearchOption.AllDirectories
        );
        Assert.Equal(2, all.Length);
    }

    [Fact(DisplayName = "S7-E2-2: 并发同字节 SaveImage 不抛不写坏，全局 blob 与会话文件各一份且可读")]
    public async Task E2_2_ConcurrentSameBytes_NoThrow_NoCorruption()
    {
        byte[] bytes = Png(7);
        const int n = 32;
        var errors = new ConcurrentBag<Exception>();
        var ids = new ConcurrentBag<string>();

        await Task.WhenAll(
            Enumerable
                .Range(0, n)
                .Select(i =>
                    Task.Run(() =>
                    {
                        try
                        {
                            var (r, _) = ImageAttachmentStore.SaveImage(
                                _dataRoot,
                                Ws,
                                SessA,
                                "same.png",
                                "image/png",
                                bytes,
                                1,
                                1
                            );
                            ids.Add(r.ImageId);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex);
                        }
                    })
                )
        );

        Assert.Empty(errors);
        Assert.Equal(n, ids.Count);
        Assert.Single(ids.Distinct());

        // 全局 blob 单份且字节完整
        string[] blobs = Directory.GetFiles(
            ImageAttachmentStore.GetGlobalAttachmentsDir(_dataRoot),
            "*",
            SearchOption.AllDirectories
        );
        Assert.Single(blobs);
        Assert.Equal(bytes, File.ReadAllBytes(blobs[0]));

        // 会话文件单份且可读回
        string[] sessionFiles = Directory.GetFiles(
            ImageAttachmentStore.GetAttachmentsDir(_dataRoot, Ws, SessA),
            "*",
            SearchOption.AllDirectories
        );
        Assert.Single(sessionFiles);
        Assert.Equal(bytes, File.ReadAllBytes(sessionFiles[0]));
        Assert.NotNull(
            ImageAttachmentStore.ResolveDataUrl(
                _dataRoot,
                Ws,
                SessA,
                new Host.Desktop.Core.Models.ChatImageRef
                {
                    ImageId = ids.First(),
                    FileName = "same.png",
                    Mime = "image/png",
                }
            )
        );
    }

    [Fact(DisplayName = "S7-E2-3: EnsureGlobalBlob 幂等（重复调用不重写、返回同一路径）")]
    public void E2_3_EnsureGlobalBlob_Idempotent()
    {
        byte[] bytes = Png(3);
        string sha16 = ImageAttachmentStore.ComputeSha16(bytes);
        string? p1 = ImageAttachmentStore.EnsureGlobalBlob(_dataRoot, sha16, "i.png", bytes);
        DateTime stamp1 = File.GetLastWriteTimeUtc(p1!);
        string? p2 = ImageAttachmentStore.EnsureGlobalBlob(_dataRoot, sha16, "i.png", bytes);
        Assert.Equal(p1, p2);
        Assert.Equal(stamp1, File.GetLastWriteTimeUtc(p2!));
        // 命中路径不再新建目录/文件
        Assert.Single(
            Directory.GetFiles(
                ImageAttachmentStore.GetGlobalAttachmentsDir(_dataRoot),
                "*",
                SearchOption.AllDirectories
            )
        );
        // 空参数零副作用
        Assert.Null(ImageAttachmentStore.EnsureGlobalBlob(_dataRoot, "", "i.png", bytes));
        Assert.Null(
            ImageAttachmentStore.EnsureGlobalBlob(_dataRoot, sha16, "i.png", Array.Empty<byte>())
        );
    }

    // ===== E3：TTL / 清理 =====

    [Fact(DisplayName = "S7-E3-1: dry-run 只统计不删（total size/count + expired 口径）")]
    public void E3_1_DryRun_StatsOnly()
    {
        byte[] bytes = Png(5, 2000);
        var (r, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "t.png",
            "image/png",
            bytes,
            1,
            1
        );
        string sessionFile = ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, r.ImageId)!;
        // 把文件时间拨到 40 天前（默认 30 天保留期 → 超期）
        File.SetLastWriteTimeUtc(sessionFile, DateTime.UtcNow.AddDays(-40));
        string blob = ImageAttachmentStore.FindGlobalBlob(_dataRoot, r.Sha16)!;
        File.SetLastWriteTimeUtc(blob, DateTime.UtcNow.AddDays(-40));

        var report = ImageAttachmentCleanup.CleanupAttachments(_dataRoot, dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(ImageAttachmentCleanup.DefaultRetentionDays, report.RetentionDays);
        Assert.Equal(2, report.ScannedFiles); // 会话文件 + 全局 blob
        Assert.Equal(2, report.ExpiredFiles);
        Assert.Equal(bytes.Length * 2L, report.ScannedBytes);
        Assert.Equal(bytes.Length * 2L, report.ExpiredBytes);
        Assert.Equal(0, report.DeletedFiles); // dry-run 不删

        // 文件都还在
        Assert.True(File.Exists(sessionFile));
        Assert.True(File.Exists(blob));
    }

    [Fact(DisplayName = "S7-E3-1b: 未超期文件不计入 expired，但计入 scanned")]
    public void E3_1b_FreshFiles_NotExpired()
    {
        var (r, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "f.png",
            "image/png",
            Png(6),
            1,
            1
        );
        var report = ImageAttachmentCleanup.CleanupAttachments(_dataRoot, dryRun: true);
        Assert.Equal(2, report.ScannedFiles);
        Assert.Equal(0, report.ExpiredFiles);
        Assert.Equal(0, report.ExpiredBytes);
        Assert.NotNull(ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, r.ImageId));
    }

    [Fact(DisplayName = "S7-E3-2: 真删超期后 FindImagePath 返回 null（转录侧标 IsMissing 的口径）")]
    public void E3_2_RealDelete_ThenMissing()
    {
        var (r, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "d.png",
            "image/png",
            Png(8),
            1,
            1
        );
        string sessionFile = ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, r.ImageId)!;
        DateTime old = DateTime.UtcNow.AddDays(-40);
        File.SetLastWriteTimeUtc(sessionFile, old);
        File.SetLastWriteTimeUtc(ImageAttachmentStore.FindGlobalBlob(_dataRoot, r.Sha16)!, old);

        var report = ImageAttachmentCleanup.CleanupAttachments(_dataRoot, dryRun: false);

        Assert.False(report.DryRun);
        Assert.Equal(2, report.DeletedFiles);
        Assert.Null(ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, r.ImageId));
        Assert.Null(ImageAttachmentStore.FindGlobalBlob(_dataRoot, r.Sha16));
        Assert.Null(ImageAttachmentStore.ResolveDataUrl(_dataRoot, Ws, SessA, r));
    }

    [Fact(DisplayName = "S7-E3-3: 真删不碰 messages.jsonl（只删附件字节）")]
    public void E3_3_RealDelete_KeepsTranscript()
    {
        string sessionDir = Path.Combine(_dataRoot, "sessions", Ws, SessA);
        Directory.CreateDirectory(sessionDir);
        string transcript = Path.Combine(sessionDir, "messages.jsonl");
        File.WriteAllText(transcript, "{\"role\":\"user\",\"content\":\"看图\"}\n");
        DateTime old = DateTime.UtcNow.AddDays(-40);
        File.SetLastWriteTimeUtc(transcript, old);

        var (r, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "k.png",
            "image/png",
            Png(9),
            1,
            1
        );
        string sessionFile = ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, r.ImageId)!;
        File.SetLastWriteTimeUtc(sessionFile, old);
        File.SetLastWriteTimeUtc(ImageAttachmentStore.FindGlobalBlob(_dataRoot, r.Sha16)!, old);

        ImageAttachmentCleanup.CleanupAttachments(_dataRoot, dryRun: false);

        Assert.True(File.Exists(transcript));
        Assert.Contains("看图", File.ReadAllText(transcript));
        Assert.False(File.Exists(sessionFile));
    }

    [Fact(DisplayName = "S7-E3-4: 限定 workspace/session 时只扫该会话（不误删别的会话）")]
    public void E3_4_ScopedCleanup_OnlyTargetSession()
    {
        DateTime old = DateTime.UtcNow.AddDays(-40);
        var (ra, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessA,
            "sa.png",
            "image/png",
            Png(11),
            1,
            1
        );
        var (rb, _) = ImageAttachmentStore.SaveImage(
            _dataRoot,
            Ws,
            SessB,
            "sb.png",
            "image/png",
            Png(12),
            1,
            1
        );
        string fa = ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, ra.ImageId)!;
        string fb = ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessB, rb.ImageId)!;
        File.SetLastWriteTimeUtc(fa, old);
        File.SetLastWriteTimeUtc(fb, old);
        File.SetLastWriteTimeUtc(ImageAttachmentStore.FindGlobalBlob(_dataRoot, ra.Sha16)!, old);
        File.SetLastWriteTimeUtc(ImageAttachmentStore.FindGlobalBlob(_dataRoot, rb.Sha16)!, old);

        var report = ImageAttachmentCleanup.CleanupAttachments(
            _dataRoot,
            dryRun: false,
            workspaceId: Ws,
            sessionId: SessA
        );

        // 限定会话时只扫该会话附件目录（不含全局 blob）：只删了 A 的 1 个文件
        Assert.Equal(1, report.ScannedFiles);
        Assert.Equal(1, report.DeletedFiles);
        Assert.Null(ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessA, ra.ImageId));
        Assert.NotNull(ImageAttachmentStore.FindImagePath(_dataRoot, Ws, SessB, rb.ImageId));
    }

    [Fact(DisplayName = "S7-E3-5: 空数据根 / 不存在的目录 → 全零报告不抛")]
    public void E3_5_EmptyRoot_ZeroReport()
    {
        string empty = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(empty);
        var report = ImageAttachmentCleanup.CleanupAttachments(empty, dryRun: false);
        Assert.Equal(0, report.ScannedFiles);
        Assert.Equal(0, report.DeletedFiles);
        Assert.Equal(ImageAttachmentCleanup.DefaultRetentionDays, report.RetentionDays);
    }

    // ===== E4：类型决策（GIF/SVG 均不进视觉链路，SVG 走 read_file） =====

    [Fact(DisplayName = "S7-E4-1: GIF 拒绝（决策：不做内联）且文案与前端同口径")]
    public void E4_1_Gif_Rejected_WithUnifiedReason()
    {
        Assert.False(ImageAttachmentStore.IsSupportedExtension("a.gif"));
        Assert.False(ImageAttachmentStore.IsSupportedExtension("a.svg"));
        Assert.Contains("仅支持 jpg/png/webp", ViewImageTool.UnsupportedTypeReason);
        Assert.Contains("read_file", ViewImageTool.UnsupportedTypeReason);
    }

    [Fact(DisplayName = "S7-E4-2: view_image 遇 svg → NOT_AN_IMAGE 且文案含 read_file 指引")]
    public async Task E4_2_Svg_NotAnImage_WithReadFileHint()
    {
        string ws = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(ws);
        File.WriteAllText(
            Path.Combine(ws, "icon.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"></svg>"
        );
        var ctx = new ToolContext
        {
            WorkspaceRoot = ws,
            WorkspaceId = Ws,
            SessionId = SessA,
            DataRoot = _dataRoot,
        };
        var result = await new ViewImageTool().ExecuteAsync(
            new ToolCallDto("call_s7_svg", "view_image", "{\"file_path\":\"icon.svg\"}"),
            ctx
        );
        Assert.False(result.Success);
        Assert.Equal(ViewImageTool.ErrorCodes.NotAnImage, result.ErrorCode);
        Assert.Contains("read_file", result.Error);
        Assert.Empty(result.Images ?? new List<Host.Desktop.Core.Models.ChatImageRef>());
    }

    [Fact(DisplayName = "S7-E4-3: 工具描述显式声明不支持 GIF/SVG（模型侧可读）")]
    public void E4_3_ToolDescription_MentionsUnsupported()
    {
        string desc = new ViewImageTool().Description;
        Assert.Contains("GIF", desc);
        Assert.Contains("SVG", desc);
        Assert.Contains("read_file", desc);
        Assert.Contains("IMAGE_TOO_LARGE", desc); // S3 错误码仍在
    }
}
