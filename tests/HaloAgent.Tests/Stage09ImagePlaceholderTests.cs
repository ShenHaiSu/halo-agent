#nullable enable
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S6 §5 验收 1~4：位置契约纯函数（<see cref="ImagePlaceholder"/>）。
/// 全部为纯函数用例（无真实文件、无临时目录、无进程静态状态）⇒ 默认可并行，不需进 Serial-* 集合。
/// 覆盖：
///   · <c>OrderImagesByPlaceholder</c>：占位符顺序排序 + 无锚点/无占位符退化（验收 1/2）
///   · <c>BuildContentBlocks</c>：交错块序列 + 被摘图就地 [image omitted]（验收 3）
///   · 旧形态 <c>[图片：fileName]</c> 文本不炸（验收 4）
/// </summary>
public sealed class Stage09ImagePlaceholderTests
{
    private static ChatImageRef Img(string id) =>
        new()
        {
            ImageId = id,
            FileName = id + ".png",
            Mime = "image/png",
            RelPath = "attachments/20260101/" + id + "_" + id + ".png",
            Width = 1,
            Height = 1,
            SizeBytes = 68,
            Sha16 = id,
        };

    private static string Dump(IEnumerable<ImageContentBlock> blocks) =>
        string.Join(",", blocks.Select(b => b.Kind + ":" + (b.Text ?? "<img>")));

    // ---- 验收 1：OrderImagesByPlaceholder 按占位符顺序排序 ----

    [Fact(DisplayName = "S6-P1: 占位符顺序覆盖数组顺序（Images=[img2,img1] → [img1,img2]）")]
    public void Order_FollowsPlaceholder()
    {
        var ordered = ImagePlaceholder.OrderImagesByPlaceholder(
            "[A][image:img1]中[image:img2]后",
            new List<ChatImageRef> { Img("img2"), Img("img1") }
        );
        Assert.Equal(new[] { "img1", "img2" }, ordered.Select(i => i.ImageId).ToArray());
    }

    [Fact(DisplayName = "S6-P2: 无占位符 → 顺序=数组序（零抖动）")]
    public void Order_NoPlaceholder_KeepsArrayOrder()
    {
        var images = new List<ChatImageRef> { Img("img_b"), Img("img_a") };
        var ordered = ImagePlaceholder.OrderImagesByPlaceholder("纯文字", images);
        Assert.Equal(new[] { "img_b", "img_a" }, ordered.Select(i => i.ImageId).ToArray());
    }

    [Fact(DisplayName = "S6-P3: 不在文本中的图沉底（保兼容，不丢）")]
    public void Order_UnknownImages_SinkToBottom()
    {
        var ordered = ImagePlaceholder.OrderImagesByPlaceholder(
            "[image:img2]文字",
            new List<ChatImageRef> { Img("img_orphan"), Img("img2") }
        );
        Assert.Equal(new[] { "img2", "img_orphan" }, ordered.Select(i => i.ImageId).ToArray());
    }

    [Fact(DisplayName = "S6-P4: 空文本/空列表/单图零抖动")]
    public void Order_DegenerateCases()
    {
        Assert.Equal(
            new[] { "img_a" },
            ImagePlaceholder
                .OrderImagesByPlaceholder(null, new List<ChatImageRef> { Img("img_a") })
                .Select(i => i.ImageId)
                .ToArray()
        );
        Assert.Empty(ImagePlaceholder.OrderImagesByPlaceholder("[image:img_a]", null));
        Assert.Empty(ImagePlaceholder.OrderImagesByPlaceholder("x", new List<ChatImageRef>()));
        // 单图走快速路径（原引用顺序）
        var single = ImagePlaceholder.OrderImagesByPlaceholder(
            "[image:img_other]",
            new List<ChatImageRef> { Img("img_only") }
        );
        Assert.Equal("img_only", Assert.Single(single).ImageId);
    }

    // ---- 验收 1：BuildContentBlocks 交错块序列 ----

    [Fact(DisplayName = "S6-P5: 三段文本两图 → text,img,text,img,text")]
    public void Blocks_Interleave()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "前[image:img1]中[image:img2]后",
            new List<ChatImageRef> { Img("img1"), Img("img2") },
            _ => "data:image/png;base64,QQ=="
        );
        Assert.Equal("Text:前,Image:<img>,Text:中,Image:<img>,Text:后", Dump(blocks));
    }

    // ---- 验收 3：被预算摘除的图占位就地变 [image omitted] ----

    [Fact(DisplayName = "S6-P6: 被摘图（resolver=null）在其占位符处渲染 [image omitted]，另一张照发")]
    public void Blocks_DroppedImage_RendersOmitted()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "前[image:img1]中[image:img2]后",
            new List<ChatImageRef> { Img("img1"), Img("img2") },
            img => img.ImageId == "img1" ? null : "data:image/png;base64,QQ=="
        );
        Assert.Equal("Text:前[image omitted]中,Image:<img>,Text:后", Dump(blocks));
    }

    [Fact(DisplayName = "S6-P7: 全部被摘 → 仍产 text 块且占位符不泄漏原名（回归：reset 回 src 的 bug）")]
    public void Blocks_AllDropped_KeepsOmittedText()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "文字仍在[image:img1]",
            new List<ChatImageRef> { Img("img1") },
            _ => null
        );
        Assert.Equal("Text:文字仍在[image omitted]", Dump(blocks));
        Assert.DoesNotContain("[image:img1]", blocks[0].Text);
    }

    [Fact(DisplayName = "S6-P8: 同 id 重复引用只发一个图块，第二处变 [image omitted]")]
    public void Blocks_DuplicateReference_EmitsOnce()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "[image:img1]与[image:img1]",
            new List<ChatImageRef> { Img("img1") },
            _ => "data:image/png;base64,QQ=="
        );
        Assert.Equal("Image:<img>,Text:与[image omitted]", Dump(blocks));
    }

    [Fact(DisplayName = "S6-P9: 锚点非空但文本占位符是未知 id → 同样渲染 [image omitted]")]
    public void Blocks_UnknownPlaceholder_RendersOmitted()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "文字仍在[image:OTHER]",
            new List<ChatImageRef> { Img("img1") },
            _ => null
        );
        Assert.Equal("Text:文字仍在[image omitted]", Dump(blocks));
    }

    // ---- 验收 2：无占位符退化文后追加 ----

    [Fact(DisplayName = "S6-P10: 无占位符 → 单 text 块 + 尾部图块（与 S6 前一致）")]
    public void Blocks_NoPlaceholder_AppendsAtEnd()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "看这张图",
            new List<ChatImageRef> { Img("img1") },
            _ => "data:image/png;base64,QQ=="
        );
        Assert.Equal("Text:看这张图,Image:<img>", Dump(blocks));
    }

    [Fact(DisplayName = "S6-P11: 无图 → 仅 text 块；空文本+空图 → 空块列表（零抖动）")]
    public void Blocks_Empty()
    {
        Assert.Equal("Text:纯文字", Dump(ImagePlaceholder.BuildContentBlocks("纯文字", null, null)));
        Assert.Empty(ImagePlaceholder.BuildContentBlocks("", new List<ChatImageRef>(), _ => "x"));
        Assert.Empty(ImagePlaceholder.BuildContentBlocks(null, null, null));
    }

    // ---- 验收 4：老数据兼容 ----

    [Fact(DisplayName = "S6-P12: 旧形态 [图片：fileName] 不参与交错，纯文本原样保留")]
    public void Blocks_LegacyPlaceholder_StaysText()
    {
        var blocks = ImagePlaceholder.BuildContentBlocks(
            "旧历史[图片：a.png]尾巴",
            new List<ChatImageRef> { Img("img1") },
            _ => "data:image/png;base64,QQ=="
        );
        // 无 [image:] 锚点 ⇒ 退化文后追加，旧文本一字不改
        Assert.Equal("Text:旧历史[图片：a.png]尾巴,Image:<img>", Dump(blocks));
    }

    [Fact(DisplayName = "S6-P13: ShortId/Marker/NormalizeMarker 形态与幂等")]
    public void Marker_ShapeAndIdempotent()
    {
        // 短 id = imageId 前 15 字符（`img_` + sha16 前若干位）；更短的 id 原样返回
        Assert.Equal("img_abcd1234fff", ImagePlaceholder.ShortId("img_abcd1234ffff"));
        Assert.Equal("[image:img_abcd1234fff]", ImagePlaceholder.Marker("img_abcd1234ffff"));
        Assert.Equal("img_abcd1234", ImagePlaceholder.ShortId("img_abcd1234"));
        Assert.Equal(string.Empty, ImagePlaceholder.Marker(null));
        Assert.Equal(string.Empty, ImagePlaceholder.Marker("  "));

        string normalized = ImagePlaceholder.NormalizeMarker("a[image:img_abcd1234ffff]b");
        Assert.Equal("a[image:img_abcd1234fff]b", normalized);
        // 幂等：再跑一次不变
        Assert.Equal(normalized, ImagePlaceholder.NormalizeMarker(normalized));
        // 旧形态不动
        Assert.Equal("[图片：a.png]", ImagePlaceholder.NormalizeMarker("[图片：a.png]"));
    }

    [Fact(DisplayName = "S6-P14: OmittedPlaceholder 与 S1 预算常量同值（跨层语义统一）")]
    public void Omitted_MatchS1Constant()
    {
        Assert.Equal(
            Host.Desktop.Services.TokenBudgetService.ImageOmittedPlaceholder,
            ImagePlaceholder.OmittedPlaceholder
        );
    }
}
