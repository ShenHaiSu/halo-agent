#nullable enable
using System;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>S1 图片估算脱钩验证（只读验证单测，先跑出 A 失败证实脱钩）。</summary>
[Collection("Serial-ImageCost")]
public sealed class Stage09ImageEstimateTests
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

    [Fact(DisplayName = "S1-A: Images 列表应计入估算（脱钩验证）")]
    public void Estimate_ImagesList_CountsTowardsTokens()
    {
        int keep = TokenBudgetService.ImageTokensPerImage;
        try
        {
            TokenBudgetService.ImageTokensPerImage = 1000;
            var withImg = UnifiedChatMessage.User("hello");
            withImg.Images = new List<ChatImageRef> { Img("img_a1"), Img("img_a2") };
            var textOnly = UnifiedChatMessage.User("hello");
            int delta =
                TokenBudgetService.EstimateMessageTokens(withImg)
                - TokenBudgetService.EstimateMessageTokens(textOnly);
            Assert.Equal(2 * TokenBudgetService.ImageTokensPerImage, delta);
        }
        finally
        {
            TokenBudgetService.ImageTokensPerImage = keep;
        }
    }

    [Fact(DisplayName = "S1-B: 纯文本估算回归（无图零抖动）")]
    public void Estimate_PureText_Unchanged()
    {
        var m1 = UnifiedChatMessage.User("hello world");
        var m2 = UnifiedChatMessage.User("hello world");
        Assert.Equal(
            TokenBudgetService.EstimateMessageTokens(m1),
            TokenBudgetService.EstimateMessageTokens(m2)
        );
        var (live, omitted) = TokenBudgetService.CountMessageImages(m1);
        Assert.Equal(0, live);
    }

    [Fact(DisplayName = "S1-C: 旧归一化文本仍按正则计1（兼容老快照）")]
    public void Estimate_LegacyNormalizedText_StillCountsOne()
    {
        int keep = TokenBudgetService.ImageTokensPerImage;
        try
        {
            TokenBudgetService.ImageTokensPerImage = 1000;
            var legacy = UnifiedChatMessage.User("看图 image:ABCDEF1234567890:800x600:image/png 说明");
            var textOnly = UnifiedChatMessage.User("看图 说明");
            // Images 为 null，老快照只靠文本正则计费。
            Assert.Null(legacy.Images);
            var (live, _) = TokenBudgetService.CountMessageImages(legacy);
            Assert.Equal(1, live);
            int delta =
                TokenBudgetService.EstimateMessageTokens(legacy)
                - TokenBudgetService.EstimateMessageTokens(textOnly);
            Assert.True(delta >= 1000, $"delta={delta}");
        }
        finally
        {
            TokenBudgetService.ImageTokensPerImage = keep;
        }
    }

    [Fact(DisplayName = "S1-D: 超预算先摘最旧 Images 列表（保留文字，不碰原对象）")]
    public void Cut_OverBudget_DropsOldestImagesListFirst()
    {
        int keep = TokenBudgetService.ImageTokensPerImage;
        try
        {
            TokenBudgetService.ImageTokensPerImage = 1000;
            var oldest = UnifiedChatMessage.User("旧图保留文字");
            oldest.Images = new List<ChatImageRef> { Img("img_old1"), Img("img_old2") };
            var newest = UnifiedChatMessage.User("新图保留文字");
            newest.Images = new List<ChatImageRef> { Img("img_new") };
            var messages = new List<UnifiedChatMessage> { oldest, newest };
            var svc = new TokenBudgetService();
            // 基线远超预算：2 张旧图(2000) + 1 张新图(1000) + 文本开销。
            var cut = svc.BuildWithinBudgetEx(messages, maxBudget: 1200);
            Assert.True(cut.ImagesTruncated);
            // 最旧整单被摘：Images 清空，Content 追加 2 个占位，文字保留。
            Assert.Null(cut.Messages[0].Images);
            Assert.Contains("旧图保留文字", cut.Messages[0].Content);
            Assert.Equal(
                2,
                TokenBudgetService.CountOmittedImagePlaceholders(cut.Messages[0].Content)
            );
            // 调用方原始消息未被修改（Clone 隔离）。
            Assert.Equal(2, oldest.Images!.Count);
            Assert.Equal("旧图保留文字", oldest.Content);
            // 最新轮保留。
            Assert.NotNull(cut.Messages[^1].Images);
            Assert.Single(cut.Messages[^1].Images!);
        }
        finally
        {
            TokenBudgetService.ImageTokensPerImage = keep;
        }
    }
}
