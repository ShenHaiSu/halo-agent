#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

// 并行禁用由 Stage01FoundationTests.cs 的 [assembly: CollectionBehavior] 全局控制。

/// <summary>
/// need22-B01 §4：技能激活展开器单测（E1~E15，纯逻辑；E14/E15 走真实 SkillMemoryService）。
/// </summary>
public sealed class Need22SkillActivationExpanderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SkillMemoryService _skills;

    public Need22SkillActivationExpanderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentNeed22Expander_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentNeed22Bootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _skills = new SkillMemoryService(_storage);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        { /* 忽略测试临时目录清理异常 */
        }
    }

    /// <summary>生产同款取数器（与 AgentOrchestrator.ResolveSkillBody 语义一致）。</summary>
    private static SkillActivationExpander.SkillBodyResolver RealResolver(
        SkillMemoryService svc,
        string? workspaceRoot
    ) =>
        (scope, name) =>
        {
            var sc = string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
                ? SkillMemoryScope.Project
                : SkillMemoryScope.Global;
            var entry = svc.Get(sc, SkillMemoryKind.Skill, name, workspaceRoot);
            if (entry == null || !entry.Enabled)
                return null; // 不存在或已停用 ⇒ 不展开
            return entry.BodyMarkdown;
        };

    // ---------- E1~E5：解析与静态工具 ----------

    [Fact(DisplayName = "N22-E1: 解析合法 marker（scope/name 正确）")]
    public void E1_ParseValidMarker()
    {
        var markers = SkillActivationExpander.ParseMarkers(
            "帮我改代码 [[skill:global/release-check]] 谢谢"
        );
        var m = Assert.Single(markers);
        Assert.Equal("global", m.Scope);
        Assert.Equal("release-check", m.Name);
        Assert.Equal("[[skill:global/release-check]]", m.Raw);
        Assert.Equal(6, m.Index);
    }

    [Fact(DisplayName = "N22-E2: 非法 scope（xxx）不匹配")]
    public void E2_ParseInvalidScope()
    {
        Assert.Empty(SkillActivationExpander.ParseMarkers("[[skill:xxx/foo]]"));
        Assert.Empty(SkillActivationExpander.ParseMarkers("[[skill:Global/foo]]"));
    }

    [Fact(DisplayName = "N22-E3: 大写 name 不匹配")]
    public void E3_ParseUpperCaseName()
    {
        Assert.Empty(SkillActivationExpander.ParseMarkers("[[skill:global/Foo]]"));
        Assert.Empty(SkillActivationExpander.ParseMarkers("[[skill:project/Release-Check]]"));
    }

    [Fact(DisplayName = "N22-E4: name 长度 1 不匹配（规则要求 ≥2）")]
    public void E4_ParseSingleCharName()
    {
        Assert.Empty(SkillActivationExpander.ParseMarkers("[[skill:global/f]]"));
    }

    [Fact(DisplayName = "N22-E5: HasMarker / StripMarkers 行为正确且保留空白")]
    public void E5_HasMarkerAndStrip()
    {
        Assert.True(SkillActivationExpander.HasMarker("a [[skill:global/foo]] b"));
        Assert.False(SkillActivationExpander.HasMarker("a [[skill:global/f]] b"));
        Assert.False(SkillActivationExpander.HasMarker(null));
        Assert.False(SkillActivationExpander.HasMarker(string.Empty));

        string stripped = SkillActivationExpander.StripMarkers("前 [[skill:global/foo]] 后");
        Assert.Equal("前  后", stripped); // 只去标记本身，空白保留
        Assert.Equal(string.Empty, SkillActivationExpander.StripMarkers(null));
    }

    // ---------- E6~E13：展开行为 ----------

    [Fact(DisplayName = "N22-E6: Expand 正常展开（marker 消失，含展开块与正文）")]
    public void E6_ExpandNormal()
    {
        var expander = new SkillActivationExpander((scope, name) => "正文内容");
        string outText = expander.Expand("改一下 [[skill:global/foo]] 的流程", null);
        Assert.Contains("<skill name=\"foo\" scope=\"global\">", outText);
        Assert.Contains("正文内容", outText);
        Assert.Contains("</skill>", outText);
        Assert.DoesNotContain("[[skill:", outText);
    }

    [Fact(DisplayName = "N22-E7: Expand 保留正文前后文本")]
    public void E7_ExpandKeepsSurroundingText()
    {
        var expander = new SkillActivationExpander((scope, name) => "BODY");
        string outText = expander.Expand("前缀[[skill:global/foo]]后缀", null);
        Assert.Equal("前缀<skill name=\"foo\" scope=\"global\">\nBODY\n</skill>后缀", outText);
    }

    [Fact(DisplayName = "N22-E8: 技能不存在 ⇒ 原样保留 marker，无展开块")]
    public void E8_ExpandMissingSkillKeepsMarker()
    {
        var expander = new SkillActivationExpander((scope, name) => null);
        string outText = expander.Expand("看 [[skill:global/nope]] 呗", null);
        Assert.Contains("[[skill:global/nope]]", outText);
        Assert.DoesNotContain("<skill ", outText);
    }

    [Fact(DisplayName = "N22-E9: 取数器抛异常 ⇒ 不抛出，原样保留 marker")]
    public void E9_ExpandResolverThrows()
    {
        var expander = new SkillActivationExpander(
            (scope, name) => throw new InvalidOperationException("boom")
        );
        string outText = expander.Expand("看 [[skill:global/foo]] 呗", null);
        Assert.Contains("[[skill:global/foo]]", outText);
        Assert.DoesNotContain("<skill ", outText);
    }

    [Fact(DisplayName = "N22-E10: project 域但无工作区根 ⇒ 保留原文")]
    public void E10_ExpandProjectWithoutWorkspaceRoot()
    {
        bool called = false;
        var expander = new SkillActivationExpander(
            (scope, name) =>
            {
                called = true;
                return "正文";
            }
        );
        string outText = expander.Expand("[[skill:project/foo]]", null);
        Assert.Contains("[[skill:project/foo]]", outText);
        Assert.DoesNotContain("<skill ", outText);
        Assert.False(called); // 不可读 ⇒ 连取数都不发生
    }

    [Fact(DisplayName = "N22-E11: 5 个不同 marker ⇒ 只展开 4 个，第 5 个保留原文并有提示行")]
    public void E11_ExpandOverLimit()
    {
        var expander = new SkillActivationExpander((scope, name) => "正文-" + name);
        string text = string.Join(
            " ",
            new[]
            {
                "[[skill:global/aa01]]",
                "[[skill:global/bb02]]",
                "[[skill:global/cc03]]",
                "[[skill:global/dd04]]",
                "[[skill:global/ee05]]",
            }
        );
        string outText = expander.Expand(text, null);
        Assert.Contains("<skill name=\"aa01\"", outText);
        Assert.Contains("<skill name=\"dd04\"", outText);
        Assert.DoesNotContain("<skill name=\"ee05\"", outText);
        Assert.Contains("[[skill:global/ee05]]", outText);
        Assert.Contains("[技能正文未全部展开：超出单条消息上限 4 个，其余技能标记保持原样]", outText);
        Assert.EndsWith("[技能正文未全部展开：超出单条消息上限 4 个，其余技能标记保持原样]", outText);
    }

    [Fact(DisplayName = "N22-E12: 同款 marker 出现 2 次 ⇒ 两处都替换为同一正文")]
    public void E12_ExpandDuplicateMarker()
    {
        var expander = new SkillActivationExpander((scope, name) => "唯一正文");
        string outText = expander.Expand("[[skill:global/foo]] 与 [[skill:global/foo]]", null);
        Assert.DoesNotContain("[[skill:", outText);
        Assert.Equal(2, outText.Split("<skill ").Length - 1);
        Assert.Equal(2, outText.Split("唯一正文").Length - 1);
    }

    [Fact(DisplayName = "N22-E13: 空文本 / null ⇒ 原样返回")]
    public void E13_ExpandEmptyText()
    {
        var expander = new SkillActivationExpander((scope, name) => "正文");
        Assert.Equal(string.Empty, expander.Expand(string.Empty, null));
        Assert.Equal(string.Empty, expander.Expand(null, null));
        Assert.Equal("没有标记的文本", expander.Expand("没有标记的文本", null));
    }

    // ---------- E14/E15：真实 SkillMemoryService 端到端 ----------

    [Fact(DisplayName = "N22-E14: 真实 SkillMemoryService 端到端 ⇒ 输出不含 frontmatter、含正文全文")]
    public void E14_RealServiceEndToEnd()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "e2e-skill",
            "# 发布检查\n第一行正文。\n第二行正文。"
        );
        var expander = new SkillActivationExpander(RealResolver(_skills, null));
        string outText = expander.Expand("[[skill:global/e2e-skill]]", null);
        Assert.Contains("<skill name=\"e2e-skill\" scope=\"global\">", outText);
        Assert.Contains("第一行正文。", outText);
        Assert.Contains("第二行正文。", outText);
        // frontmatter 不进正文
        Assert.DoesNotContain("version:", outText);
        Assert.DoesNotContain("scope_hint", outText);
        Assert.DoesNotContain("updated_at", outText);
        Assert.DoesNotContain("---", outText);
    }

    [Fact(DisplayName = "N22-E15: 已停用技能（enabled=false）⇒ 不展开")]
    public void E15_DisabledSkillNotExpanded()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "off-skill",
            "停用正文",
            enabled: false
        );
        var expander = new SkillActivationExpander(RealResolver(_skills, null));
        string outText = expander.Expand("[[skill:global/off-skill]]", null);
        Assert.Contains("[[skill:global/off-skill]]", outText);
        Assert.DoesNotContain("<skill ", outText);
        Assert.DoesNotContain("停用正文", outText);
    }
}
