#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-02：稳定装配线验收（Skill/Memory L0 常驻 + 高缓存命中）。</summary>
public sealed class Stage09StablePromptTests
{
    private static (PromptConfigDocument cfg, PromptContext ctx) Defaults()
    {
        var cfg = PromptDefaults.CreateDefault();
        var ctx = new PromptContext(
            "Microsoft Windows NT 10.0.22631.0",
            "X64",
            "ws",
            "C:\\proj",
            "Python 3.11.9 (C:\\Python311\\python.exe)",
            "2026-01-01"
        );
        return (cfg, ctx);
    }

    private static string SkillBlock() =>
        SystemPromptBuilder.BuildSkillIndexBlock(
            new[]
            {
                new SystemPromptBuilder.StableSkillEntry(
                    "global",
                    "git-helper",
                    "常用 git 流程与冲突处理",
                    5
                ),
                new SystemPromptBuilder.StableSkillEntry(
                    "project",
                    "api-convention",
                    "本项目接口命名与错误码规范",
                    9
                ),
            }
        );

    private static string MemBlock() =>
        SystemPromptBuilder.BuildMemoryPinnedBlock(
            new[]
            {
                new SystemPromptBuilder.StableMemoryEntry(
                    "project",
                    "project-brief",
                    "本项目是 Windows 桌面 Agent。",
                    8
                ),
                new SystemPromptBuilder.StableMemoryEntry("global", "user-pref", "用户偏好中文回复。", 3),
            }
        );

    [Fact(DisplayName = "S09-02: 同快照两次 BuildCore 逐字节一致且顺序冻结")]
    public void SameSnapshot_TwoBuilds_ByteIdentical_And_OrderFrozen()
    {
        var (cfg, ctx) = Defaults();
        string skill = SkillBlock();
        string mem = MemBlock();
        var (a1, b1, _) = SystemPromptBuilder.BuildCore(cfg, ctx, skill, mem);
        var (a2, b2, _) = SystemPromptBuilder.BuildCore(cfg, ctx, skill, mem);
        Assert.Equal(a1, a2);
        Assert.Equal(
            HeaderVariableResolver.ComputeDeterministicHash16(a1),
            HeaderVariableResolver.ComputeDeterministicHash16(a2)
        );
        var ids = b1.Select(b => b.BlockId).ToList();
        Assert.Equal(
            new[]
            {
                "identity",
                "runtime",
                "cmdPython",
                "toolGuide",
                "skillIndex",
                "memoryPinned",
                "ephemeral"
            },
            ids
        );
        Assert.True(
            b1.Where(b => b.BlockId is "skillIndex" or "memoryPinned").All(b => b.IsStable)
        );
        // 块级顺序（行首标题定位，避免命中 toolGuide 指南正文中的 ## 5./## 6. 引用）
        int i3 = a1.IndexOf("\n## 3.", StringComparison.Ordinal);
        int i5 = a1.IndexOf("\n" + SystemPromptBuilder.SkillIndexTitle, StringComparison.Ordinal);
        int i6 = a1.IndexOf("\n" + SystemPromptBuilder.MemoryPinnedTitle, StringComparison.Ordinal);
        int i4 = a1.IndexOf(PrefixCachePredictor.EphemeralMarker, StringComparison.Ordinal);
        Assert.True(i3 >= 0 && i5 > i3 && i6 > i5 && i4 > i6);
        Assert.Contains("skill_tool", a1);
        Assert.EndsWith("\n", a1);
        Assert.DoesNotContain("\r\n", a1);
    }

    [Fact(DisplayName = "S09-02: 空域整块消失且缺省输出逐字节一致")]
    public void EmptySkillMemory_BlocksVanish_And_DefaultByteIdentical()
    {
        var (cfg, ctx) = Defaults();
        var (def, defBlocks, _) = SystemPromptBuilder.BuildCore(cfg, ctx);
        var (e1, _, _) = SystemPromptBuilder.BuildCore(cfg, ctx, null, null);
        var (e2, _, _) = SystemPromptBuilder.BuildCore(cfg, ctx, "", "   ");
        var (e3, _, _) = SystemPromptBuilder.BuildCore(cfg, ctx, "\r\n  \r\n", null);
        Assert.Equal(def, e1);
        Assert.Equal(def, e2);
        Assert.Equal(def, e3);
        Assert.DoesNotContain(defBlocks, b => b.BlockId == "skillIndex");
        Assert.DoesNotContain(defBlocks, b => b.BlockId == "memoryPinned");
        Assert.False(Regex.IsMatch(def, "^## 5\\.", RegexOptions.Multiline));
        Assert.False(Regex.IsMatch(def, "^## 6\\.", RegexOptions.Multiline));
    }

    [Fact(DisplayName = "S09-02: 索引排序与 priority 无关（global 先 + name 升序），两次一致")]
    public void SkillIndex_SortStable_RegardlessOfPriority()
    {
        var entries = new[]
        {
            new SystemPromptBuilder.StableSkillEntry("project", "b-skill", "低优先级但名字靠前", 1),
            new SystemPromptBuilder.StableSkillEntry("global", "c-skill", "高优先级全局", 100),
            new SystemPromptBuilder.StableSkillEntry("global", "a-skill", "中等优先级全局", 50),
            new SystemPromptBuilder.StableSkillEntry("project", "a-skill", "同名 project 域", 60),
        };
        string first = SystemPromptBuilder.BuildSkillIndexBlock(entries);
        string second = SystemPromptBuilder.BuildSkillIndexBlock(entries.Reverse().ToArray());
        Assert.Equal(first, second);
        var rows = first
            .Split('\n')
            .Where(l => l.StartsWith("- [", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(
            new[]
            {
                "- [global] a-skill — 中等优先级全局",
                "- [global] c-skill — 高优先级全局",
                "- [project] a-skill — 同名 project 域",
                "- [project] b-skill — 低优先级但名字靠前",
            },
            rows
        );
    }

    [Fact(DisplayName = "S09-02: 超限截断高 priority 保留 + 冻结截断行 + 确定性")]
    public void SkillIndex_Truncation_KeepsHighPriority_WithFrozenFooter()
    {
        var entries = Enumerable
            .Range(0, 6)
            .Select(i => new SystemPromptBuilder.StableSkillEntry(
                "global",
                $"skill-{i}",
                new string((char)('a' + i), 40) + " 说明文字填充长度",
                Priority: i
            ))
            .ToList();
        string a = SystemPromptBuilder.BuildSkillIndexBlock(entries, tokenBudget: 120);
        string b = SystemPromptBuilder.BuildSkillIndexBlock(entries, tokenBudget: 120);
        Assert.Equal(a, b);
        var m = Regex.Match(a, @"\(…and (\d+) more skills omitted; list via skill_tool\)");
        Assert.True(m.Success);
        int omitted = int.Parse(m.Groups[1].Value);
        var rows = a.Split('\n').Where(l => l.StartsWith("- [", StringComparison.Ordinal)).ToList();
        Assert.Equal(entries.Count - omitted, rows.Count);
        Assert.Contains(rows, r => r.Contains("skill-5")); // 最高 priority 保留
        Assert.DoesNotContain("skill-0", a); // 最低 priority 被截
        // 渲染顺序仍是 name 升序（与 priority 无关）
        var names = rows.Select(r => r.Split(' ').ElementAt(2)).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
    }

    [Fact(DisplayName = "S09-02: Memory 同名 project 覆盖 global 且按 name 升序")]
    public void MemoryPinned_ProjectOverridesGlobal_SortedByName()
    {
        string block = SystemPromptBuilder.BuildMemoryPinnedBlock(
            new[]
            {
                new SystemPromptBuilder.StableMemoryEntry("global", "brief", "GLOBAL 正文（应被覆盖）", 1),
                new SystemPromptBuilder.StableMemoryEntry("project", "brief", "PROJECT 正文（胜出）", 1),
                new SystemPromptBuilder.StableMemoryEntry("global", "aaa-first", "排序靠前的条目", 1),
            }
        );
        Assert.Contains("PROJECT 正文（胜出）", block);
        Assert.DoesNotContain("GLOBAL 正文", block);
        int iAaa = block.IndexOf("### [global] aaa-first", StringComparison.Ordinal);
        int iBrief = block.IndexOf("### [project] brief", StringComparison.Ordinal);
        Assert.True(iAaa >= 0 && iBrief > iAaa);
        string again = SystemPromptBuilder.BuildMemoryPinnedBlock(
            new[]
            {
                new SystemPromptBuilder.StableMemoryEntry("global", "brief", "GLOBAL 正文（应被覆盖）", 1),
                new SystemPromptBuilder.StableMemoryEntry("project", "brief", "PROJECT 正文（胜出）", 1),
                new SystemPromptBuilder.StableMemoryEntry("global", "aaa-first", "排序靠前的条目", 1),
            }
        );
        Assert.Equal(block, again);
    }

    [Fact(DisplayName = "S09-02: Predictor 物理仍 5 块，skill/mem 变更精确归因")]
    public void Predictor_SkillOrMemoryOnlyChange_DivergesFinely_PhysicalStillFive()
    {
        PrefixCachePredictor.ClearForTests();
        var msgs = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") };
        string pre = "IDENT\n## 1. Runtime\n- cmd.exe\n";
        string skillA =
            SystemPromptBuilder.SkillIndexTitle
            + "\n- [global] a — x\n"
            + SystemPromptBuilder.SkillIndexFooter
            + "\n";
        string skillB =
            SystemPromptBuilder.SkillIndexTitle
            + "\n- [global] b — y\n"
            + SystemPromptBuilder.SkillIndexFooter
            + "\n";
        string memA = SystemPromptBuilder.MemoryPinnedTitle + "\n### [global] m\nbody-a\n";
        string memB = SystemPromptBuilder.MemoryPinnedTitle + "\n### [global] m\nbody-b\n";
        string eph = "\n## 4. Session Ephemeral\n- date\n";
        var sBase = PrefixCachePredictor.BuildSnapshot(pre + skillA + memA + eph, msgs, null);
        var sSkill = PrefixCachePredictor.BuildSnapshot(pre + skillB + memA + eph, msgs, null);
        var sMem = PrefixCachePredictor.BuildSnapshot(pre + skillA + memB + eph, msgs, null);
        Assert.Equal(5, sBase.Blocks.Count);
        Assert.Equal("skillIndex", PrefixCachePredictor.Compare(sBase, sSkill).DivergenceBlock);
        Assert.Equal("memoryPinned", PrefixCachePredictor.Compare(sBase, sMem).DivergenceBlock);
        var same = PrefixCachePredictor.Compare(
            sBase,
            PrefixCachePredictor.BuildSnapshot(pre + skillA + memA + eph, msgs, null),
            0.5
        );
        Assert.True(same.IsStable);
    }

    [Fact(DisplayName = "S09-02: 图片开销约1000/张可配，占位后只按文本计费")]
    public void Budget_ImageCost_Configurable_And_OmittedIsTextOnly()
    {
        int keep = TokenBudgetService.ImageTokensPerImage;
        try
        {
            TokenBudgetService.ImageTokensPerImage = 1000;
            var withImg = UnifiedChatMessage.User("看图 image:ABCDEF1234567890:800x600:image/png 说明");
            var textOnly = UnifiedChatMessage.User("看图 说明");
            int delta =
                TokenBudgetService.EstimateMessageTokens(withImg)
                - TokenBudgetService.EstimateMessageTokens(textOnly);
            Assert.True(delta >= 1000, $"delta={delta}");
            var omitted = UnifiedChatMessage.User("看图 [image omitted] 说明");
            int delta2 =
                TokenBudgetService.EstimateMessageTokens(omitted)
                - TokenBudgetService.EstimateMessageTokens(textOnly);
            Assert.True(delta2 < 100, $"delta2={delta2}");
        }
        finally
        {
            TokenBudgetService.ImageTokensPerImage = keep;
        }
    }

    [Fact(DisplayName = "S09-02: 超预算先丢最旧图占位（保留文字，不碰原对象）")]
    public void Budget_OverBudget_DropsOldestImageFirst()
    {
        int keep = TokenBudgetService.ImageTokensPerImage;
        try
        {
            TokenBudgetService.ImageTokensPerImage = 1000;
            var old = UnifiedChatMessage.User("旧图 image:AAAAAAAAAAAAAAAA:100x100:image/png 保留文字");
            var latest = UnifiedChatMessage.User("最新结论");
            var messages = new List<UnifiedChatMessage> { old, latest };
            var svc = new TokenBudgetService();
            var cut = svc.BuildWithinBudgetEx(messages, maxBudget: 60);
            Assert.True(cut.ImagesTruncated);
            Assert.DoesNotContain("image:AAAAAAAAAAAAAAAA", cut.Messages[0].Content);
            Assert.Contains(TokenBudgetService.ImageOmittedPlaceholder, cut.Messages[0].Content);
            Assert.Contains("保留文字", cut.Messages[0].Content);
            Assert.Contains("image:AAAAAAAAAAAAAAAA", old.Content); // 原对象不动
            Assert.Equal("最新结论", cut.Messages[^1].Content);
        }
        finally
        {
            TokenBudgetService.ImageTokensPerImage = keep;
        }
    }

    [Fact(DisplayName = "S09-02: LastSent stableHash/fullHash 双记与两者对比")]
    public void Store_StableFullHash_CompareWithPreview()
    {
        PromptLastSentStore.ClearForTests();
        const string sid = "s09-store";
        Assert.Null(PromptLastSentStore.Get(sid));
        PromptLastSentStore.Record(
            sid,
            new LastSentPromptSnapshot
            {
                ReqId = "r1",
                SessionId = sid,
                WorkspaceId = "w",
                PrefixHash = "FULL",
                StableHash = "STABLE",
                FullHash = "FULL",
                CharLength = 10,
                SentAtUtc = "t",
            }
        );
        var same = PromptLastSentStore.CompareWithPreview(sid, "STABLE", "FULL");
        Assert.True(same.HasLastSent && same.StableEqual && same.FullEqual);
        var skillChanged = PromptLastSentStore.CompareWithPreview(sid, "STABLE2", "FULL2");
        Assert.True(
            skillChanged.HasLastSent && !skillChanged.StableEqual && !skillChanged.FullEqual
        );
        var unknown = PromptLastSentStore.CompareWithPreview("nope", "STABLE", "FULL");
        Assert.False(unknown.HasLastSent);
        PromptLastSentStore.ClearForTests();
    }
}
