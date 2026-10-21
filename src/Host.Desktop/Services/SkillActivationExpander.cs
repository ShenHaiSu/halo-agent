#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Services;

/// <summary>need22-B01：技能激活标记展开器（marker → readme 正文全文）。</summary>
public sealed class SkillActivationExpander
{
    /// <summary>标记前缀（与前端 shared/utils/skillMarker.ts 同源冻结）。</summary>
    public const string MarkerPrefix = "[[skill:";

    /// <summary>单条消息最多展开的技能数（超出保留原文，防止上下文暴涨）。</summary>
    public const int MaxExpandedSkillsPerMessage = 4;

    /// <summary>超限时追加的提示行（冻结文案）。</summary>
    public const string OverflowNotice = "[技能正文未全部展开：超出单条消息上限 4 个，其余技能标记保持原样]";

    /// <summary>标记语法：[[skill:&lt;scope&gt;/&lt;name&gt;]]；name 规则与 NormalizeAndValidateName 对齐。</summary>
    private static readonly Regex MarkerRegex =
        new(
            @"\[\[skill:(global|project)/([a-z0-9][a-z0-9-_]{1,47})\]\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

    /// <summary>单个 marker（Raw = 原始标记文本；Index = 在原文中的起始下标）。</summary>
    public sealed record SkillMarker(string Scope, string Name, string Raw, int Index);

    /// <summary>取数器：按 (scope, name) 取正文；返回 null 表示不存在/不可读。</summary>
    public delegate string? SkillBodyResolver(string scope, string name);

    private readonly SkillBodyResolver? _resolver;

    /// <summary>need22-B01：无参构造 = 无取数器（所有 marker 保留原文）；生产环境由调用方注入取数器。</summary>
    public SkillActivationExpander(SkillBodyResolver? resolver = null)
    {
        _resolver = resolver;
    }

    /// <summary>解析文本中的全部 marker（按出现顺序；非法语法不匹配）。</summary>
    public static IReadOnlyList<SkillMarker> ParseMarkers(string? text)
    {
        var result = new List<SkillMarker>();
        if (string.IsNullOrEmpty(text))
            return result;
        foreach (Match m in MarkerRegex.Matches(text))
        {
            result.Add(new SkillMarker(m.Groups[1].Value, m.Groups[2].Value, m.Value, m.Index));
        }
        return result;
    }

    /// <summary>文本是否含 marker（快路径）。</summary>
    public static bool HasMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (!text.Contains(MarkerPrefix, StringComparison.Ordinal))
            return false;
        return MarkerRegex.IsMatch(text);
    }

    /// <summary>去掉 marker 本身（保留相邻空白）；用于标题/摘要/TOC。</summary>
    public static string StripMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return MarkerRegex.Replace(text, string.Empty);
    }

    /// <summary>
    /// need22-B01：展开 marker → &lt;skill …&gt;正文&lt;/skill&gt;；失败保留原文。永不抛异常。
    /// 规则：空文本原样返回；无 marker 原样返回；同款 (scope,name) 全部替换为同一份正文；
    /// 成功展开数超上限则超出部分保留原文并在文末追加提示行；project 域缺工作区根视为不可读。
    /// </summary>
    public string Expand(string? text, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        IReadOnlyList<SkillMarker> markers = ParseMarkers(text);
        if (markers.Count == 0)
            return text; // 零开销快路径

        var sb = new StringBuilder(text.Length + 512);
        // (scope,name) → 展开结果；null = 取数失败（保留原文）；OverLimit = 超上限未展开。
        var cache = new Dictionary<string, ExpansionOutcome>(StringComparer.Ordinal);
        int expandedCount = 0;
        bool overflow = false;
        int cursor = 0;

        foreach (SkillMarker marker in markers)
        {
            sb.Append(text, cursor, marker.Index - cursor);
            cursor = marker.Index + marker.Raw.Length;

            string key = marker.Scope + "/" + marker.Name;
            if (!cache.TryGetValue(key, out ExpansionOutcome? outcome))
            {
                if (expandedCount >= MaxExpandedSkillsPerMessage)
                {
                    // 超出单条消息上限：保留原文，不再取数。
                    outcome = new ExpansionOutcome(overLimit: true);
                }
                else
                {
                    string? block = ResolveBlock(marker, workspaceRoot);
                    outcome =
                        block == null
                            ? new ExpansionOutcome(overLimit: false)
                            : new ExpansionOutcome(block);
                }
                cache[key] = outcome;
            }

            if (outcome.OverLimit)
            {
                overflow = true;
                sb.Append(marker.Raw);
                continue;
            }
            if (outcome.Block == null)
            {
                // 取数失败：原样写入 marker 文本（已在 ResolveBlock 内 Warn）。
                sb.Append(marker.Raw);
                continue;
            }
            if (!outcome.Counted)
            {
                expandedCount++;
                outcome.Counted = true;
            }
            sb.Append(outcome.Block);
        }

        sb.Append(text, cursor, text.Length - cursor);

        if (overflow)
            sb.Append('\n').Append(OverflowNotice);

        return sb.ToString();
    }

    /// <summary>need22-B01：取正文并构造展开块；失败（null/异常/project 缺根）返回 null 并 Warn。</summary>
    private string? ResolveBlock(SkillMarker marker, string? workspaceRoot)
    {
        if (
            string.Equals(marker.Scope, "project", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(workspaceRoot)
        )
        {
            // project 域缺工作区根 ⇒ 视为不可读（保留原文）。
            HaloLogger.App.Warn($"[SkillActivation] 展开失败：{marker.Scope}/{marker.Name}");
            return null;
        }

        string? body = null;
        try
        {
            body = _resolver?.Invoke(marker.Scope, marker.Name);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[SkillActivation] 展开失败：{marker.Scope}/{marker.Name}", ex);
            return null;
        }

        if (body == null)
        {
            HaloLogger.App.Warn($"[SkillActivation] 展开失败：{marker.Scope}/{marker.Name}");
            return null;
        }

        return BuildSkillBlock(marker.Scope, marker.Name, body);
    }

    /// <summary>need22-B01：展开块格式（冻结）。</summary>
    private static string BuildSkillBlock(string scope, string name, string body)
    {
        var sb = new StringBuilder();
        sb.Append("<skill name=\"")
            .Append(name)
            .Append("\" scope=\"")
            .Append(scope)
            .Append("\">\n");
        sb.Append(body.TrimEnd('\r', '\n'));
        sb.Append("\n</skill>");
        return sb.ToString();
    }

    /// <summary>need22-B01：同款 marker 的展开结果缓存。</summary>
    private sealed class ExpansionOutcome
    {
        public string? Block { get; }
        public bool OverLimit { get; }
        public bool Counted { get; set; }

        public ExpansionOutcome(string? block = null, bool overLimit = false)
        {
            Block = block;
            OverLimit = overLimit;
        }
    }
}
