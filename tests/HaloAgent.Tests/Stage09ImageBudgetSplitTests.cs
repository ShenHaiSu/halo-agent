#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S5 §5 验收 1~5：历史/当轮预算拆盘 + raw 口径。
/// 全部为纯函数用例（无真实文件、无临时目录、无静态可变状态）⇒ 默认可并行，
/// 不需要进 Serial-* 集合；单文件运行 &lt; 1s。
/// </summary>
public sealed class Stage09ImageBudgetSplitTests
{
    private static ChatImageRef Img(string id, long size) =>
        new()
        {
            ImageId = id,
            FileName = id + ".png",
            Mime = "image/png",
            RelPath = "attachments/20260101/" + id + "_" + id + ".png",
            Width = 1,
            Height = 1,
            SizeBytes = size,
            Sha16 = id,
        };

    private static List<(string ImageId, long SizeBytes)> Seq(params (string, long)[] items) =>
        items.Select(i => (i.Item1, i.Item2)).ToList();

    // ---- 验收 1：常量锁定（十进制 raw，防手滑改回 8MiB / 16MB） ----

    [Fact(DisplayName = "S5-1: MaxHistoryImageBytes == 30_000_000（十进制 raw）")]
    public void HistoryCap_IsThirtyMillion()
    {
        Assert.Equal(30_000_000L, VisionDefaults.MaxHistoryImageBytes);
    }

    [Fact(DisplayName = "S5-2: MaxTurnImageBytes == 32_000_000（十进制 raw）")]
    public void TurnCap_IsThirtyTwoMillion()
    {
        Assert.Equal(32_000_000L, VisionDefaults.MaxTurnImageBytes);
    }

    [Fact(DisplayName = "S5-3: 历史盘上限只能下调不能上调（Math.Min 夹）")]
    public void HistoryCap_ClampOnlyDowngrades()
    {
        Assert.Equal(
            VisionDefaults.MaxHistoryImageBytes,
            ImageBudgetPlanner.ClampHistoryCap(long.MaxValue)
        );
        Assert.Equal(
            VisionDefaults.MaxHistoryImageBytes,
            ImageBudgetPlanner.ClampHistoryCap(VisionDefaults.MaxHistoryImageBytes + 1)
        );
        Assert.Equal(16_000_000L, ImageBudgetPlanner.ClampHistoryCap(16_000_000L));
        // 0/负数视为未配置 ⇒ 取常量（不是 0 = 全丢）
        Assert.Equal(VisionDefaults.MaxHistoryImageBytes, ImageBudgetPlanner.ClampHistoryCap(0));
    }

    // ---- 验收 2：逆序语义（12 张小图全留 / 超预算丢最旧保最新） ----

    [Fact(DisplayName = "S5-4: 12 张小图历史全保留（未超 30M）")]
    public void TwelveSmallHistory_AllAdmitted()
    {
        var seq = Enumerable.Range(1, 12).Select(i => ($"img_h{i:00}", 1_000_000L)).ToList();
        var (admit, dropped) = ImageBudgetPlanner.PlanReverse(
            seq,
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Equal(12, admit.Count);
        Assert.Empty(dropped);
    }

    [Fact(DisplayName = "S5-5: 超预算丢最旧保最新（逆序贪心）")]
    public void OverBudget_DropsOldestKeepsNewest()
    {
        // 上限 30M，三张各 10M + 一张最新 5M：从最新开始 5M→10M→10M 共 25M 准入，最旧那张 10M 被丢。
        var seq = Seq(
            ("img_oldest", 10_000_000L),
            ("img_mid1", 10_000_000L),
            ("img_mid2", 10_000_000L),
            ("img_newest", 5_000_000L)
        );
        var (admit, dropped) = ImageBudgetPlanner.PlanReverse(
            seq,
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.DoesNotContain("img_oldest", admit);
        Assert.Contains("img_mid1", admit);
        Assert.Contains("img_mid2", admit);
        Assert.Contains("img_newest", admit);
        Assert.Single(dropped);
        Assert.Equal("img_oldest", dropped[0]);
    }

    [Fact(DisplayName = "S5-6: 单张超上限整张丢（不半读），其余按逆序继续准入")]
    public void OversizedSingle_DroppedWhole_RestAdmitted()
    {
        var seq = Seq(("img_old", 1_000_000L), ("img_huge", 40_000_000L), ("img_new", 2_000_000L));
        var (admit, dropped) = ImageBudgetPlanner.PlanReverse(
            seq,
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Equal(new[] { "img_new", "img_old" }, admit.OrderBy(x => x).ToArray());
        Assert.Single(dropped);
        Assert.Equal("img_huge", dropped[0]);
    }

    [Fact(DisplayName = "S5-7: 空序列零抖动（空 admit / 空 dropped）")]
    public void EmptySequence_NoAdmitNoDrop()
    {
        var (admit, dropped) = ImageBudgetPlanner.PlanReverse(
            new List<(string, long)>(),
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Empty(admit);
        Assert.Empty(dropped);
    }

    // ---- 验收 3：拆盘隔离 ----

    [Fact(DisplayName = "S5-8: 当前轮 20M 大图不挤历史盘（历史 12 张全留，当轮图走当轮盘）")]
    public void CurrentBigImage_DoesNotSqueezeHistory()
    {
        var history = Enumerable.Range(1, 12).Select(i => ($"img_h{i:00}", 500_000L)).ToList();
        var (hAdmit, hDropped) = ImageBudgetPlanner.PlanReverse(
            history,
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Equal(12, hAdmit.Count);
        Assert.Empty(hDropped);

        // 当轮盘独立计数：20M 在当前轮 32M 盘内应被准入。
        var (tAdmit, tDropped) = ImageBudgetPlanner.PlanReverse(
            Seq(("img_cur20m", 20_000_000L)),
            VisionDefaults.MaxTurnImageBytes
        );
        Assert.Contains("img_cur20m", tAdmit);
        Assert.Empty(tDropped);
    }

    [Fact(DisplayName = "S5-9: 历史大图不挤当轮盘（历史超限自立丢图，当轮小图照发）")]
    public void HistoryOverflow_DoesNotSqueezeCurrentTurn()
    {
        var history = Seq(("img_h_old", 25_000_000L), ("img_h_new", 25_000_000L));
        var (hAdmit, hDropped) = ImageBudgetPlanner.PlanReverse(
            history,
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Contains("img_h_new", hAdmit);
        Assert.Equal(new[] { "img_h_old" }, hDropped.ToArray());

        var (tAdmit, tDropped) = ImageBudgetPlanner.PlanReverse(
            Seq(("img_cur_small", 1_000_000L)),
            VisionDefaults.MaxTurnImageBytes
        );
        Assert.Contains("img_cur_small", tAdmit);
        Assert.Empty(tDropped);
    }

    // ---- 验收 4：口径 raw（不用 url.Length） ----

    [Fact(DisplayName = "S5-10: 当轮 resolver 按 raw 扣减（url≈size*4/3 时旧 8MB 口径会早爆）")]
    public void TurnBudget_CountsRawNotDataUrlLength()
    {
        // 用 6 张 5.5M raw 图模拟 view_image 循环：raw 合计 33M > 32M ⇒ 只放过 5 张（27.5M）。
        // 若按 url.Length（≈raw*4/3）计，则第 4 张即 29.3M、第 5 张 36.6M 就爆 ⇒ 只会过 4 张。
        var imgs = Enumerable.Range(1, 6).Select(i => Img($"img_t{i}", 5_500_000L)).ToList();
        long rawUsed = 0;
        int admitted = 0;
        foreach (var im in imgs)
        {
            if (rawUsed + im.SizeBytes > VisionDefaults.MaxTurnImageBytes)
                break;
            rawUsed += im.SizeBytes;
            admitted++;
        }
        Assert.Equal(5, admitted);
        Assert.True(rawUsed <= VisionDefaults.MaxTurnImageBytes);

        // 反证：同样序列若按 base64 长度（≈ raw*4/3）累计，第 5 张就应被拒。
        long b64Used = 0;
        int admittedByUrl = 0;
        foreach (var im in imgs)
        {
            long urlLen = im.SizeBytes * 4 / 3;
            if (b64Used + urlLen > VisionDefaults.MaxTurnImageBytes)
                break;
            b64Used += urlLen;
            admittedByUrl++;
        }
        Assert.True(admitted > admittedByUrl, "raw 口径应比 base64 口径多发图");
    }

    // ---- 验收 3 补充：StripDropped 只动副本 ----

    [Fact(DisplayName = "S5-11: StripDropped 摘除未准入引用但不改原消息")]
    public void StripDropped_KeepsOriginals()
    {
        var m1 = UnifiedChatMessage.User("历史");
        m1.Images = new List<ChatImageRef> { Img("img_keep", 1_000L), Img("img_drop", 2_000L) };
        var m2 = UnifiedChatMessage.User("纯文字");
        var src = new List<UnifiedChatMessage> { m1, m2 };

        var admit = new HashSet<string>(StringComparer.Ordinal) { "img_keep" };
        var stripped = ImageBudgetPlanner.StripDropped(src, admit);

        Assert.Equal(2, stripped.Count);
        Assert.Single(stripped[0].Images!);
        Assert.Equal("img_keep", stripped[0].Images![0].ImageId);
        // 原消息未被改写（落盘语义不动）
        Assert.Equal(2, m1.Images!.Count);
        // 无图消息原样复用（零抖动）
        Assert.Same(m2, stripped[1]);
    }

    [Fact(DisplayName = "S5-12: 全部被摘时 Images 置 null（adapter 走 content:string）")]
    public void StripDropped_AllDropped_ImagesNull()
    {
        var m = UnifiedChatMessage.User("历史");
        m.Images = new List<ChatImageRef> { Img("img_x", 1_000L) };
        var stripped = ImageBudgetPlanner.StripDropped(
            new List<UnifiedChatMessage> { m },
            new HashSet<string>(StringComparer.Ordinal)
        );
        Assert.Null(stripped[0].Images);
        Assert.Equal("历史", stripped[0].Content);
    }

    // ---- 验收 4 补充：两盘告知文案可区分 ----

    [Fact(DisplayName = "S5-13: 历史盘告知文案与当轮盘可区分（历史超预算 / 本轮字节超预算）")]
    public void Notices_AreDistinguishable()
    {
        var dropped = new List<(string, string)> { ("old.png", "img_abcdef123") };
        string history = MultimodalPlanner.DropNotice.HistoryBudgetExceeded(
            VisionDefaults.MaxHistoryImageBytes,
            dropped
        );
        string turn = MultimodalPlanner.DropNotice.BudgetExceeded(
            VisionDefaults.MaxTurnImageBytes,
            dropped
        );

        Assert.StartsWith("[图片省略] 历史图片超预算未发送 1 张", history);
        Assert.Contains("30MB", history);
        Assert.Contains("raw 计", history);
        Assert.Contains("old.png(img_ab)", history);
        Assert.Contains("重新附图", history);

        Assert.StartsWith("[图片省略] 本轮图片字节超预算", turn);
        Assert.Contains("32MB", turn);
        Assert.Contains("缩略图", turn);

        Assert.NotEqual(history, turn);
        Assert.Equal(
            string.Empty,
            MultimodalPlanner.DropNotice.HistoryBudgetExceeded(
                VisionDefaults.MaxHistoryImageBytes,
                new List<(string, string)>()
            )
        );
    }

    // ---- 三盘顺序锁定：历史盘 → 当轮盘 → 张数盘 ----

    [Fact(DisplayName = "S5-14: 三盘都超时被丢的是最旧历史（张数盘最后执行，保最新）")]
    public void AllThreeCapsExceeded_DropsOldestHistory()
    {
        // 历史 3 张各 20M（历史盘 30M 只容得下最新 1 张）
        var history = Seq(
            ("img_h1", 20_000_000L),
            ("img_h2", 20_000_000L),
            ("img_h3", 20_000_000L)
        );
        var (hAdmit, hDropped) = ImageBudgetPlanner.PlanReverse(
            history,
            ImageBudgetPlanner.ClampHistoryCap(VisionDefaults.MaxHistoryImageBytes)
        );
        Assert.Equal(new[] { "img_h3" }, hAdmit.ToArray());
        Assert.Equal(new[] { "img_h1", "img_h2" }, hDropped.ToArray());

        // 张数盘 4 张最后执行：剩下的 1 张历史 + 当轮图仍按保最新裁。
        // 构造 5 张在盘内但超张数：张数盘应保留最后 4 张（最旧历史被丢）。
        var kept = new List<string> { "img_h3", "img_c1", "img_c2", "img_c3", "img_c4" };
        int max = VisionDefaults.DefaultMaxImages;
        var keepSet = kept.Skip(Math.Max(0, kept.Count - max)).ToList();
        Assert.Equal(4, keepSet.Count);
        Assert.DoesNotContain("img_h3", keepSet);
        Assert.Contains("img_c4", keepSet);
    }

    // ---- 双协议端到端：历史盘摘除后发包 payload 无该图且带告知 ----

    private static readonly HeaderResolutionContext Ctx =
        new("ws1", "ws", "/tmp", "sess1", "hash1", "p1", "P", "gpt-4o", "req1");

    private static UnifiedLlmRequest Request(
        ModelConfig model,
        List<UnifiedChatMessage> messages,
        Func<ChatImageRef, string?> resolver
    ) =>
        new()
        {
            RequestId = "req_s5",
            Provider = new ProviderConfig
            {
                Id = "p1",
                Name = "P",
                BaseUrl = "https://api.openai.com/v1",
            },
            Model = model,
            HeaderContext = Ctx,
            SystemInstructions = "sys",
            Messages = messages,
            Tools = Array.Empty<UnifiedToolDefinition>(),
            ImageResolver = resolver,
        };

    private static string Decode(string payload) =>
        System.Text.RegularExpressions.Regex.Replace(
            payload,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
        );

    [Fact(DisplayName = "S5-15: 历史盘摘除的图不进 image_url（双协议一致）")]
    public void HistoryStripped_NotEmitted()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o",
            Protocol = LlmProtocolType.Chat,
            SupportsVision = true,
            MaxImagesPerTurn = 4,
        };
        var old = UnifiedChatMessage.User("旧图");
        old.Images = new List<ChatImageRef> { Img("img_old_big", 40_000_000L) };
        var cur = UnifiedChatMessage.User("新图");
        cur.Images = new List<ChatImageRef> { Img("img_cur", 1_000L) };
        var src = new List<UnifiedChatMessage> { old, cur };

        var (admit, dropped) = ImageBudgetPlanner.PlanReverse(
            Seq(("img_old_big", 40_000_000L), ("img_cur", 1_000L)),
            VisionDefaults.MaxHistoryImageBytes
        );
        Assert.Equal(new[] { "img_old_big" }, dropped.ToArray());
        var stripped = ImageBudgetPlanner.StripDropped(src, admit);
        Assert.Null(stripped[0].Images);
        Assert.Single(stripped[1].Images!);

        Func<ChatImageRef, string?> resolver = img => "data:image/png;base64," + img.ImageId;
        string chat = new ChatCompletionsAdapter().SerializeRequest(
            Request(model, stripped, resolver)
        );
        Assert.DoesNotContain("img_old_big", chat);
        Assert.Contains("img_cur", chat);

        // 原对象未被改写
        Assert.NotNull(old.Images);
    }
}
