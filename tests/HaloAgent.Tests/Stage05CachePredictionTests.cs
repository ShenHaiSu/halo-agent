#nullable enable
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need05-06：本地预测 + 分块拼接对比（推测算法与拼接对比算法验收）。</summary>
public sealed class Stage05CachePredictionTests
{
    private static string StableSys(string ephemeral = "2026-01-01") =>
        "You are Halo-Agent\n## 1. Runtime\n- cmd.exe\n\n## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n- Current Date: "
        + ephemeral
        + "\n";

    private static UnifiedLlmRequest Req(string sys, List<UnifiedChatMessage> msgs) =>
        new()
        {
            RequestId = "r",
            Provider = new ProviderConfig
            {
                Id = "p",
                Name = "P",
                BaseUrl = "https://x/v1"
            },
            Model = new ModelConfig { Id = "m", Protocol = LlmProtocolType.Chat },
            HeaderContext = new HeaderResolutionContext(
                "w",
                "n",
                "p",
                "s",
                "h",
                "p",
                "pn",
                "m",
                "r"
            ),
            SystemInstructions = sys,
            Messages = msgs,
            Tools = new List<UnifiedToolDefinition>
            {
                UnifiedToolDefinition.Create("a_tool", "a", "{}")
            },
        };

    [Fact(DisplayName = "S05-06: 全等快照判定 stable，预测沿用实测")]
    public void Identical_Snapshots_Are_Stable()
    {
        PrefixCachePredictor.ClearForTests();
        var msgs = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("ok")
        };
        var s1 = PrefixCachePredictor.BuildSnapshot(StableSys(), msgs, null);
        var s2 = PrefixCachePredictor.BuildSnapshot(StableSys(), msgs, null);
        var diff = PrefixCachePredictor.Compare(s1, s2, 0.75);
        Assert.True(diff.IsStable);
        Assert.Equal("stable", diff.DivergenceBlock);
        Assert.Equal(0.75, diff.ExpectedHitRate!.Value, 6);
        Assert.Equal(s1.TotalEst, diff.StableTokens);
    }

    [Fact(DisplayName = "S05-06: 追加最新问答只断裂 latest/history，稳定段非零")]
    public void Append_Only_Keeps_Prefix_Stable()
    {
        PrefixCachePredictor.ClearForTests();
        var prev = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("q1"),
            UnifiedChatMessage.Assistant("a1")
        };
        var cur = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("q1"),
            UnifiedChatMessage.Assistant("a1"),
            UnifiedChatMessage.User("q2")
        };
        var sPrev = PrefixCachePredictor.BuildSnapshot(StableSys(), prev, null);
        var sCur = PrefixCachePredictor.BuildSnapshot(StableSys(), cur, null);
        var diff = PrefixCachePredictor.Compare(sPrev, sCur);
        Assert.False(diff.IsStable);
        Assert.True(diff.StableTokens > 0);
        Assert.True(diff.ExpectedHitRate > 0 && diff.ExpectedHitRate < 1.0);
        // 断裂应在 historyPrefix 或 latest，不应在 system.stable
        Assert.NotEqual("system.stable", diff.DivergenceBlock);
    }

    [Fact(DisplayName = "S05-06: 首块 system.stable 变化预测归零")]
    public void Stable_Head_Change_Predicts_Zero()
    {
        var prev = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") };
        var cur = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") };
        var sPrev = PrefixCachePredictor.BuildSnapshot("SYS-V1", prev, null);
        var sCur = PrefixCachePredictor.BuildSnapshot("SYS-V2-CHANGED", cur, null);
        var diff = PrefixCachePredictor.Compare(sPrev, sCur);
        Assert.Equal("system.stable", diff.DivergenceBlock);
        Assert.Equal(0, diff.StableTokens);
        Assert.Equal(0.0, diff.ExpectedHitRate!.Value, 6);
    }

    [Fact(DisplayName = "S05-06: 仅日期尾部变化命中率不归零")]
    public void Ephemeral_Only_Change_Keeps_High_Prediction()
    {
        var msgs = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("hi"),
            UnifiedChatMessage.Assistant("ok")
        };
        var sPrev = PrefixCachePredictor.BuildSnapshot(StableSys("2026-01-01"), msgs, null);
        var sCur = PrefixCachePredictor.BuildSnapshot(StableSys("2026-01-02"), msgs, null);
        var diff = PrefixCachePredictor.Compare(sPrev, sCur);
        Assert.Equal("system.ephemeral", diff.DivergenceBlock);
        Assert.True(diff.StableTokens > 0);
        Assert.True(diff.ExpectedHitRate > 0.3);
    }

    [Fact(DisplayName = "S05-06: 首轮无依据不瞎猜 + 字节 LCP + EWMA")]
    public void First_Round_Null_And_Lcp_Ewma()
    {
        var msgs = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") };
        var s = PrefixCachePredictor.BuildSnapshot(StableSys(), msgs, null);
        var diff = PrefixCachePredictor.Compare(null, s);
        Assert.True(diff.IsFirst);
        Assert.Null(diff.ExpectedHitRate);

        Assert.Equal(3, PrefixCachePredictor.ComputeByteLcpLength("abcdef", "abcXYZ"));
        Assert.Equal(0, PrefixCachePredictor.ComputeByteLcpLength("a", "b"));
        Assert.Equal(0, PrefixCachePredictor.ComputeByteLcpLength(null, "b"));

        Assert.Equal(0.5, PrefixCachePredictor.SmoothEwma(null, 0.5), 9);
        Assert.Equal(0.3 * 1.0 + 0.7 * 0.5, PrefixCachePredictor.SmoothEwma(0.5, 1.0), 9);
    }

    [Fact(DisplayName = "S05-06: TrackRequest/RecordActual 存储往返 + DTO 透传预测")]
    public void Store_Roundtrip_Feeds_Budget_Dto()
    {
        PrefixCachePredictor.ClearForTests();
        const string sessionHash = "sess-hash-06";
        var m1 = new List<UnifiedChatMessage> { UnifiedChatMessage.User("q1") };
        var m2 = new List<UnifiedChatMessage>
        {
            UnifiedChatMessage.User("q1"),
            UnifiedChatMessage.User("q2")
        };
        var s1 = PrefixCachePredictor.BuildSnapshot(StableSys(), m1, null);
        var (hit1, _, _) = PrefixCachePredictor.TrackRequest(sessionHash, "sid", s1);
        Assert.Null(hit1); // 首轮无依据

        PrefixCachePredictor.RecordActual(sessionHash, "sid", 1000, 750);
        var s2 = PrefixCachePredictor.BuildSnapshot(StableSys(), m2, null);
        var (hit2, cached2, div2) = PrefixCachePredictor.TrackRequest(sessionHash, "sid", s2);
        Assert.NotNull(hit2);
        Assert.True(cached2 > 0);
        Assert.False(string.IsNullOrWhiteSpace(div2));

        var svc = new TokenBudgetService();
        var dto = svc.CalculateBudgetStatus(
            new ModelConfig
            {
                Id = "m",
                Protocol = LlmProtocolType.Chat,
                MaxContextTokens = 128000
            },
            new List<UnifiedChatMessage>(),
            sessionHash
        );
        Assert.NotNull(dto.PredictedCacheHitRate);
        Assert.Equal(hit2!.Value, dto.PredictedCacheHitRate!.Value, 6);
        Assert.Equal(div2, dto.PrefixDivergenceBlock);
        PrefixCachePredictor.ClearForTests();
    }

    [Fact(DisplayName = "S05-06: 工具乱序不影响工具块哈希（拼接稳定）")]
    public void Tools_Order_Insensitive_Hash()
    {
        var t1 = new List<UnifiedToolDefinition>
        {
            UnifiedToolDefinition.Create("zeta", "z", "{}"),
            UnifiedToolDefinition.Create("alpha", "a", "{}"),
        };
        var t2 = new List<UnifiedToolDefinition>
        {
            UnifiedToolDefinition.Create("alpha", "a", "{}"),
            UnifiedToolDefinition.Create("zeta", "z", "{}"),
        };
        Assert.Equal(
            PrefixCachePredictor.CanonicalToolsHash(t1),
            PrefixCachePredictor.CanonicalToolsHash(t2)
        );
    }
}
