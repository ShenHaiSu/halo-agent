#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// S3 §5 验收 3~4：熔断与张数截断的「发包文本显式告知」。
/// 断言只看发包 payload 文本，落盘历史不受影响（告知只进内存副本）。
/// </summary>
public sealed class Stage09ImageDropNoticeTests
{
    private static readonly HeaderResolutionContext Ctx =
        new("ws1", "ws", "/tmp", "sess1", "hash1", "p1", "P", "gpt-4o", "req1");

    private static UnifiedLlmRequest Request(
        ModelConfig model,
        List<UnifiedChatMessage> messages,
        Func<ChatImageRef, string?>? resolver
    ) =>
        new()
        {
            RequestId = "req_s3",
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

    /// <summary>payload 是 JSON，中文会转义成 \uXXXX；断言前先反转义成可读文本。</summary>
    private static string Decode(string payload) =>
        System.Text.RegularExpressions.Regex.Replace(
            payload,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
        );

    // ---- 验收 4：张数截断告知 ----

    [Fact(DisplayName = "S3-N1: MaxImagesPerTurn=2 + 4 张 → 发包文本含「另有 2 张…未发送」")]
    public void Chat_Truncated_NotifiesDroppedCount()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o",
            Protocol = LlmProtocolType.Chat,
            SupportsVision = true,
            MaxImagesPerTurn = 2,
        };
        var m1 = UnifiedChatMessage.User("第一批图");
        m1.Images = new List<ChatImageRef> { Img("img_a"), Img("img_b") };
        var m2 = UnifiedChatMessage.User("第二批图");
        m2.Images = new List<ChatImageRef> { Img("img_c"), Img("img_d") };

        string payload = new ChatCompletionsAdapter().SerializeRequest(
            Request(
                model,
                new List<UnifiedChatMessage> { m1, m2 },
                _ => "data:image/png;base64,QUJD"
            )
        );

        string decoded = Decode(payload);
        Assert.Contains("另有 2 张因超 MaxImagesPerTurn(2)未发送", decoded);
        Assert.Contains("实发 2 张", decoded);
        // 张数算法不变：仍保最新丢最旧（a/b 最旧被丢）。
        // 被丢空的那条 user 已无图 ⇒ content 回落为 string（零抖动语义），告知句挂在它上面。
        using var doc = JsonDocument.Parse(payload);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        var firstContent = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.String, firstContent.ValueKind);
        string firstUserText = firstContent.GetString()!;
        Assert.Contains("第一批图", firstUserText);
        Assert.Contains("另有 2 张因超 MaxImagesPerTurn(2)未发送", firstUserText);
        // 最新两张仍实发（该用例 resolver 不把 imageId 写进 URL，故按张数断言）。
        int emitted = 0;
        int idx = 0;
        while (
            (idx = payload.IndexOf("\"type\":\"image_url\"", idx, StringComparison.Ordinal)) >= 0
        )
        {
            emitted++;
            idx += 5;
        }
        Assert.Equal(2, emitted);
    }

    [Fact(DisplayName = "S3-N2: Responses 协议同样下发截断告知")]
    public void Resp_Truncated_NotifiesDroppedCount()
    {
        var model = new ModelConfig
        {
            Id = "o3",
            Protocol = LlmProtocolType.Resp,
            SupportsVision = true,
            MaxImagesPerTurn = 1,
        };
        var m = UnifiedChatMessage.User("三张图");
        m.Images = new List<ChatImageRef> { Img("img_x"), Img("img_y"), Img("img_z") };

        string payload = new ResponsesApiAdapter().SerializeRequest(
            Request(model, new List<UnifiedChatMessage> { m }, _ => "data:image/png;base64,QUJD")
        );
        Assert.Contains("另有 2 张因超 MaxImagesPerTurn(1)未发送", Decode(payload));
    }

    [Fact(DisplayName = "S3-N3: 未超限时零告知（无多余文本抖动）")]
    public void UnderLimit_NoNotice()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o",
            Protocol = LlmProtocolType.Chat,
            SupportsVision = true,
            MaxImagesPerTurn = 4,
        };
        var m = UnifiedChatMessage.User("两张图");
        m.Images = new List<ChatImageRef> { Img("img_1"), Img("img_2") };

        string payload = new ChatCompletionsAdapter().SerializeRequest(
            Request(model, new List<UnifiedChatMessage> { m }, _ => "data:image/png;base64,QUJD")
        );
        Assert.DoesNotContain("MaxImagesPerTurn", payload);
        Assert.DoesNotContain("另有", payload);
    }

    // ---- 验收 3：熔断告知（resolver 返回 null 模拟超预算，Planner 侧丢弃） ----

    [Fact(DisplayName = "S3-N4: resolver 返回 null 的图不进 image_url，正文照发")]
    public void NullDataUrl_ImageOmitted_TextKept()
    {
        var model = new ModelConfig
        {
            Id = "gpt-4o",
            Protocol = LlmProtocolType.Chat,
            SupportsVision = true,
            MaxImagesPerTurn = 4,
        };
        var m = UnifiedChatMessage.User("文字必须保留");
        m.Images = new List<ChatImageRef> { Img("img_drop") };

        string payload = new ChatCompletionsAdapter().SerializeRequest(
            Request(model, new List<UnifiedChatMessage> { m }, _ => null)
        );
        Assert.DoesNotContain("image_url", payload);
        Assert.Contains("文字必须保留", Decode(payload));
    }

    // ---- DropNotice 文案单元（熔断句自身） ----

    [Fact(DisplayName = "S3-N5: BudgetExceeded 列出 imageId 前 6 位与文件名")]
    public void BudgetExceeded_ListsSkippedIds()
    {
        string text = MultimodalPlanner.DropNotice.BudgetExceeded(
            8L * 1024 * 1024,
            new List<(string, string)> { ("a.png", "img_abcdef123"), ("b.png", "img_xyz") }
        );
        Assert.Contains("[图片省略]", text);
        Assert.Contains("8MB", text);
        Assert.Contains("2 张图未发送", text);
        Assert.Contains("a.png(img_ab)", text);
        Assert.Contains("b.png(img_xy)", text);
        Assert.Contains("可改用缩略图/小图重试", text);
    }

    [Fact(DisplayName = "S3-N6: 空 skipped 不产生告知句")]
    public void BudgetExceeded_Empty_NoText()
    {
        Assert.Equal(
            string.Empty,
            MultimodalPlanner.DropNotice.BudgetExceeded(
                8L * 1024 * 1024,
                new List<(string, string)>()
            )
        );
        Assert.Equal(string.Empty, MultimodalPlanner.DropNotice.CountTruncated(4, 0, 4));
    }

    [Fact(DisplayName = "S3-N7: 非视觉剥离文案含模型与张数")]
    public void NonVisionStripped_Text()
    {
        string text = MultimodalPlanner.DropNotice.NonVisionStripped("deepseek-chat", 3);
        Assert.StartsWith("[图片已忽略]", text);
        Assert.Contains("deepseek-chat", text);
        Assert.Contains("3 张图片未发送", text);
        Assert.Contains("文字照常发送", text);
    }

    // ---- 验收 5：非视觉剥离 → 发包文本含 [图片已忽略] 且 Images 已剥 ----

    [Fact(DisplayName = "S3-N8: 非视觉剥离 → 首条前置 [图片已忽略]，图全剥，原文不动")]
    public void StripForNonVision_PrependsNotice_StripsImages()
    {
        var m1 = UnifiedChatMessage.User("第一句");
        m1.Images = new List<ChatImageRef> { Img("img_nv1") };
        var m2 = UnifiedChatMessage.User("第二句");
        m2.Images = new List<ChatImageRef> { Img("img_nv2"), Img("img_nv3") };
        var src = new List<UnifiedChatMessage> { m1, m2 };

        var (messages, stripped) = MultimodalPlanner.DropNotice.StripForNonVision(
            src,
            "deepseek-chat"
        );

        Assert.Equal(3, stripped);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Null(m.Images));
        Assert.StartsWith("[图片已忽略]", messages[0].Content);
        Assert.Contains("deepseek-chat", messages[0].Content);
        Assert.Contains("3 张图片未发送", messages[0].Content);
        Assert.Contains("第一句", messages[0].Content);
        // 原文对象不被改写（落盘历史不受影响）。
        Assert.Same(m1, src[0]);
        Assert.NotNull(src[0].Images);
        Assert.NotNull(src[1].Images);
    }

    [Fact(DisplayName = "S3-N9: 无图时 StripForNonVision 零抖动（原引用返回）")]
    public void StripForNonVision_NoImages_NoChange()
    {
        var src = new List<UnifiedChatMessage> { UnifiedChatMessage.User("纯文字") };
        var (messages, stripped) = MultimodalPlanner.DropNotice.StripForNonVision(src, "m");
        Assert.Equal(0, stripped);
        Assert.Same(src, messages);
    }

    // ---- 验收 3：熔断告知（字节超预算）并入发包副本 ----

    [Fact(DisplayName = "S3-N10: 熔断 → 末条追加 [图片省略] 并列出被跳 imageId 前 6 位")]
    public void WithBudgetNotice_AppendsToLast()
    {
        var m1 = UnifiedChatMessage.User("第一句");
        var m2 = UnifiedChatMessage.User("第二句");
        var src = new List<UnifiedChatMessage> { m1, m2 };
        var skipped = new List<(string, string)>
        {
            ("huge1.png", "img_aaa111"),
            ("huge2.png", "img_bbb222"),
        };

        var messages = MultimodalPlanner.DropNotice.WithBudgetNotice(
            src,
            8L * 1024 * 1024,
            skipped
        );

        Assert.Equal(2, messages.Count);
        Assert.Equal("第一句", messages[0].Content); // 非末条不动
        Assert.Contains("第二句", messages[1].Content);
        Assert.Contains("[图片省略]", messages[1].Content);
        Assert.Contains("8MB", messages[1].Content);
        Assert.Contains("2 张图未发送", messages[1].Content);
        Assert.Contains("img_aa", messages[1].Content);
        Assert.Contains("img_bb", messages[1].Content);
        Assert.Contains("huge1.png", messages[1].Content);
        // 原对象不被改写。
        Assert.Equal("第二句", m2.Content);
    }

    [Fact(DisplayName = "S3-N11: 无跳过图时熔断零抖动（原引用返回）")]
    public void WithBudgetNotice_Empty_NoChange()
    {
        var src = new List<UnifiedChatMessage> { UnifiedChatMessage.User("纯文字") };
        var messages = MultimodalPlanner.DropNotice.WithBudgetNotice(
            src,
            8L * 1024 * 1024,
            new List<(string, string)>()
        );
        Assert.Same(src, messages);
    }
}
