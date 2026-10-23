using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Services;

public sealed partial class SystemPromptBuilder
{
    /// <summary>need09-02：L0 索引条目（解耦 DTO：只用原始类型，不引用 SkillMemoryService 任何类型）。</summary>
    public sealed record StableSkillEntry(string Scope, string Name, string Summary, int Priority);

    /// <summary>need09-02：常驻记忆条目（解耦 DTO：只用原始类型）。</summary>
    public sealed record StableMemoryEntry(string Scope, string Name, string Body, int Priority);

    /// <summary>need09-02：块体规范化：\r\n→\n + TrimEnd（幂等；无 \r 输入时与老 TrimEnd 逐字节一致）。</summary>
    public static string NormalizeBlockBody(string? body) =>
        (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd();

    /// <summary>need09-02：summary 规范化：Trim + \n→空格 + 超 60 字截断+…（冻结；按 Unicode 标量计数，防代理对切断）。</summary>
    public static string NormalizeSkillSummary(string? summary, int maxChars = SkillSummaryMaxChars)
    {
        string s = (summary ?? string.Empty).Trim().Replace("\n", " ");
        if (s.Length == 0)
            return string.Empty;
        int runes = 0;
        foreach (var r in s.EnumerateRunes())
            runes++;
        if (runes <= maxChars)
            return s;
        return string.Concat(s.EnumerateRunes().Take(maxChars)) + "…";
    }

    internal static int StableScopeRank(string? scope) =>
        string.Equals(scope?.Trim(), "global", StringComparison.OrdinalIgnoreCase)
            ? 0
            : string.Equals(scope?.Trim(), "project", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;

    /// <summary>
    /// need09-02：渲染 L0 Skill 索引块（空域返回 string.Empty 由 BuildCore 整块丢弃）。
    /// 行格式冻结：`- [&lt;scope&gt;] &lt;name&gt; — &lt;summary≤60字&gt;`；排序 scope(global先)→name升序（与 priority 无关）；
    /// 超限时按 priority 高保留 + name 升序截断并追加冻结截断行，渲染顺序仍保持稳定排序。
    /// </summary>
    public static string BuildSkillIndexBlock(
        IEnumerable<StableSkillEntry>? entries,
        int tokenBudget = SkillIndexTokenBudget
    )
    {
        var list = (entries ?? Enumerable.Empty<StableSkillEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => new StableSkillEntry(
                string.IsNullOrWhiteSpace(e.Scope) ? "global" : e.Scope.Trim(),
                e.Name.Trim(),
                NormalizeSkillSummary(e.Summary),
                e.Priority
            ))
            .ToList();
        if (list.Count == 0)
            return string.Empty;
        List<StableSkillEntry> StableOrder(IEnumerable<StableSkillEntry> src) =>
            src.OrderBy(e => StableScopeRank(e.Scope))
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        string RenderRow(StableSkillEntry e) =>
            ("- [" + e.Scope + "] " + e.Name + " — " + e.Summary).TrimEnd();
        string Render(IReadOnlyList<StableSkillEntry> rows, int omitted)
        {
            var lines = new List<string>(rows.Count + 3) { SkillIndexTitle };
            foreach (var r in rows)
                lines.Add(RenderRow(r));
            if (omitted > 0)
                lines.Add(string.Format(SkillTruncatedLineFormat, omitted));
            lines.Add(SkillIndexFooter);
            return string.Join("\n", lines);
        }
        var stable = StableOrder(list);
        if (TokenBudgetService.EstimateTokens(Render(stable, 0)) <= tokenBudget)
            return Render(stable, 0);
        var byPriority = list.OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var kept = new List<StableSkillEntry>();
        foreach (var e in byPriority)
        {
            var trial = new List<StableSkillEntry>(kept) { e };
            if (
                TokenBudgetService.EstimateTokens(
                    Render(StableOrder(trial), list.Count - trial.Count)
                ) <= tokenBudget
            )
                kept = trial;
        }
        return Render(StableOrder(kept), list.Count - kept.Count);
    }

    /// <summary>
    /// need09-02：渲染常驻 Memory 块（空域返回 string.Empty 由 BuildCore 整块丢弃）。
    /// 同名去重：project 覆盖 global（整条替换）；排序按 name 升序（与 priority 无关）；
    /// 超限截断同 Skill（高 priority 保留 + 冻结截断行）。
    /// </summary>
    public static string BuildMemoryPinnedBlock(
        IEnumerable<StableMemoryEntry>? entries,
        int tokenBudget = MemoryPinnedTokenBudget
    )
    {
        var list = (entries ?? Enumerable.Empty<StableMemoryEntry>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => new StableMemoryEntry(
                string.IsNullOrWhiteSpace(e.Scope) ? "global" : e.Scope.Trim(),
                e.Name.Trim(),
                NormalizeBlockBody(e.Body),
                e.Priority
            ))
            .Where(e => !string.IsNullOrEmpty(e.Body))
            .ToList();
        if (list.Count == 0)
            return string.Empty;
        var deduped = list.GroupBy(e => e.Name, StringComparer.Ordinal)
            .Select(g =>
                g.OrderBy(e =>
                        string.Equals(e.Scope, "project", StringComparison.OrdinalIgnoreCase)
                            ? 0
                            : string.Equals(e.Scope, "global", StringComparison.OrdinalIgnoreCase)
                                ? 1
                                : 2
                    )
                    .ThenByDescending(e => e.Priority)
                    .First()
            )
            .ToList();
        List<StableMemoryEntry> StableOrder(IEnumerable<StableMemoryEntry> src) =>
            src.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        string Render(IReadOnlyList<StableMemoryEntry> rows, int omitted)
        {
            var lines = new List<string>(rows.Count * 2 + 2) { MemoryPinnedTitle };
            foreach (var r in rows)
            {
                lines.Add("### [" + r.Scope + "] " + r.Name);
                lines.Add(r.Body);
            }
            if (omitted > 0)
                lines.Add(string.Format(MemoryTruncatedLineFormat, omitted));
            return string.Join("\n", lines);
        }
        var stable = StableOrder(deduped);
        if (TokenBudgetService.EstimateTokens(Render(stable, 0)) <= tokenBudget)
            return Render(stable, 0);
        var byPriority = deduped
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var kept = new List<StableMemoryEntry>();
        foreach (var e in byPriority)
        {
            var trial = new List<StableMemoryEntry>(kept) { e };
            if (
                TokenBudgetService.EstimateTokens(
                    Render(StableOrder(trial), deduped.Count - trial.Count)
                ) <= tokenBudget
            )
                kept = trial;
        }
        return Render(StableOrder(kept), deduped.Count - kept.Count);
    }

    /// <summary>need16：渲染 Session Goal 块（空/无内容返回 string.Empty 由 BuildCore 整块丢弃，stable=true）。</summary>
    public static string BuildGoalBlock(Host.Desktop.Core.Models.SessionGoal? goal)
    {
        if (goal == null || !goal.HasContent)
            return string.Empty;
        var ms = goal.Milestones ?? new List<Host.Desktop.Core.Models.GoalMilestone>();
        int total = ms.Count;
        int done = ms.Count(m => m.Done);
        var lines = new List<string>();
        lines.Add("## 5. Session Goal（本轮目标，goal_tool 可改）");
        if (!string.IsNullOrWhiteSpace(goal.Title))
            lines.Add("- 标题：" + goal.Title.Trim());
        if (!string.IsNullOrWhiteSpace(goal.Description))
            lines.Add("- 描述：" + goal.Description.Trim());
        lines.Add(
            $"- 进度：{goal.Percent}%（{done}/{total} 里程碑）"
                + (string.IsNullOrWhiteSpace(goal.Note) ? string.Empty : goal.Note.Trim())
        );
        int show = 0;
        foreach (var m in ms)
        {
            if (show >= 20)
                break;
            lines.Add("- [" + (m.Done ? "x" : " ") + "] " + (m.Title ?? string.Empty).Trim());
            show++;
        }
        if (total > show)
            lines.Add($"…余{total - show}条");
        lines.Add("Goal 变更经 goal_tool 下一轮生效；子会话只读。");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// need09-02：唯一拼装路径。装配顺序（冻结）：custom(head) → identity → runtime → cmdPython → toolGuide →
    /// skillIndex → memoryPinned → ephemeral → custom(tail)。skill/memory 为渲染好的 string 块（B 轨道不引用
    /// SkillMemoryService 类型）；null/空/空白视为空域，整块（含标题）直接消失；缺省即老输出逐字节一致。
    /// </summary>
    /// need17-06 §6.2：新增可选 activeToolNames（本轮激活集；null=全量 Direct 工具）。装配顺序冻结：通用纪律 → 工具段 → 规则段。
    public static (
        string assembled,
        List<PromptBlockPreview> blocks,
        List<string> unknownVars
    ) BuildCore(
        PromptConfigDocument cfg,
        PromptContext ctx,
        string? skillIndexBlock = null,
        string? memoryPinnedBlock = null,
        string? goalBlock = null,
        ISet<string>? activeToolNames = null
    )
    {
        var blocks = new List<PromptBlockPreview>();
        var unknown = new List<string>();
        if (!cfg.EnableHaloPrefix)
            return (string.Empty, blocks, unknown);

        var ordered = new List<(string id, string text, bool stable, bool enabled)>();
        string customPlacement = string.Equals(
            cfg.CustomPlacement,
            "head",
            StringComparison.OrdinalIgnoreCase
        )
            ? "head"
            : "tail";
        string customRendered = string.Empty;
        bool hasCustom = cfg.EnableCustom && !string.IsNullOrWhiteSpace(cfg.CustomText);
        if (hasCustom)
            customRendered = RenderTemplate(cfg.CustomText, ctx, unknown).TrimEnd();

        if (hasCustom && customPlacement == "head")
            ordered.Add(("custom", customRendered, true, true));
        if (cfg.EnableIdentity && !string.IsNullOrWhiteSpace(cfg.IdentityText))
            ordered.Add(("identity", (cfg.IdentityText ?? string.Empty).TrimEnd(), true, true));
        if (cfg.EnableRuntime)
        {
            string rt = RenderTemplate(cfg.RuntimeTemplate ?? string.Empty, ctx, unknown).TrimEnd();
            if (!string.IsNullOrWhiteSpace(rt))
                ordered.Add(("runtime", rt, true, true));
        }
        if (cfg.EnableCmdPython && !string.IsNullOrWhiteSpace(cfg.CmdPythonText))
        {
            // need17-07：快照缺失时追加冻结英文注记（stable=true 不变，prefix-cache 稳定）
            string cmdPy = (cfg.CmdPythonText ?? string.Empty).TrimEnd();
            if (!Host.Desktop.Tools.Process.PythonAvailabilitySnapshot.Current.Found)
                cmdPy +=
                    "\nNOTE: Python was NOT detected on this machine. Do NOT use runner:'python'; use 'cmd'. If the user needs Python, guide them to install it from python.org and restart HaloAgent.";
            ordered.Add(("cmdPython", cmdPy, true, true));
        }
        if (cfg.EnableToolGuide)
        {
            string tg = RenderTemplate(cfg.ToolGuideTemplate ?? string.Empty, ctx, unknown)
                .TrimEnd();
            if (!string.IsNullOrWhiteSpace(tg))
            {
                tg =
                    NormalizeBlockBody(tg)
                    + "\n"
                    + SkillMemoryGuideSuffix
                    + BuildNeed17DynamicSuffix(activeToolNames);
                ordered.Add(("toolGuide", tg, true, true));
            }
        }
        string skillNorm = NormalizeBlockBody(skillIndexBlock);
        if (!string.IsNullOrWhiteSpace(skillNorm))
            ordered.Add(("skillIndex", skillNorm, true, true));
        string memNorm = NormalizeBlockBody(memoryPinnedBlock);
        if (!string.IsNullOrWhiteSpace(memNorm))
            ordered.Add(("memoryPinned", memNorm, true, true));
        string goalNorm = NormalizeBlockBody(goalBlock);
        if (!string.IsNullOrWhiteSpace(goalNorm))
            ordered.Add(("goal", goalNorm, true, true));
        if (cfg.EnableEphemeral)
        {
            string eph = RenderTemplate(cfg.EphemeralTemplate ?? string.Empty, ctx, unknown)
                .TrimEnd();
            if (!string.IsNullOrWhiteSpace(eph))
            {
                if (!eph.Contains(PrefixCachePredictor.EphemeralMarker, StringComparison.Ordinal))
                    eph = "## 4. Session Ephemeral\n" + eph.TrimStart();
                ordered.Add(("ephemeral", eph, false, true));
            }
        }
        if (hasCustom && customPlacement == "tail")
            ordered.Add(("custom", customRendered, false, true));

        foreach (var (id, text, stable, _) in ordered)
        {
            blocks.Add(
                new PromptBlockPreview
                {
                    BlockId = id,
                    Enabled = true,
                    RenderedText = text,
                    EstimatedTokens = TokenBudgetService.EstimateTokens(text),
                    IsStable = stable,
                }
            );
        }

        // 关断块也记录（Enabled=false），满足 T2 断言 Blocks 全 Disabled
        if (blocks.Count == 0 && cfg.EnableHaloPrefix)
        {
            foreach (
                string id in new[]
                {
                    "identity",
                    "runtime",
                    "cmdPython",
                    "toolGuide",
                    "skillIndex",
                    "memoryPinned",
                    "ephemeral",
                    "custom"
                }
            )
                blocks.Add(
                    new PromptBlockPreview
                    {
                        BlockId = id,
                        Enabled = false,
                        RenderedText = string.Empty,
                        EstimatedTokens = 0,
                        IsStable = !string.Equals(id, "ephemeral", StringComparison.Ordinal)
                    }
                );
            return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
        }
        if (!cfg.EnableHaloPrefix)
            return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());

        string assembled =
            ordered.Count == 0
                ? string.Empty
                : string.Join("\n\n", ordered.Select(p => NormalizeBlockBody(p.text))) + "\n";
        return (assembled, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
    }

    private static PromptContext CreateContext(
        WorkspaceInfo ws,
        string? pyOverride,
        string? dateOverride
    )
    {
        string osVersion =
            RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.VersionString;
        string arch = RuntimeInformation.OSArchitecture.ToString();
        string wsName = string.IsNullOrWhiteSpace(ws.Name) ? "workspace" : ws.Name.Trim();
        string wsRoot = string.IsNullOrWhiteSpace(ws.RootPath)
            ? Environment.CurrentDirectory
            : ws.RootPath;
        string pythonStatus = string.IsNullOrWhiteSpace(pyOverride)
            ? GetPythonStatus()
            : pyOverride.Trim();
        string today = string.IsNullOrWhiteSpace(dateOverride)
            ? DateTime.Now.ToString("yyyy-MM-dd")
            : dateOverride.Trim();
        return new PromptContext(osVersion, arch, wsName, wsRoot, pythonStatus, today);
    }

    internal static string RenderTemplate(
        string template,
        PromptContext ctx,
        List<string>? unknownSink = null
    )
    {
        string s = template ?? string.Empty;
        s = s.Replace("{{OsVersion}}", ctx.OsVersion)
            .Replace("{{OsArch}}", ctx.OsArch)
            .Replace("{{WorkspaceName}}", ctx.WsName)
            .Replace("{{WorkspaceRoot}}", ctx.WsRoot)
            .Replace("{{PythonStatus}}", ctx.PythonStatus)
            .Replace("{{CurrentDate}}", ctx.Today);
        foreach (Match m in UnknownVarRegex.Matches(s))
        {
            string name = m.Groups[1].Value;
            unknownSink?.Add(name);
        }
        return s;
    }

    private static bool IsDefaultConfig(PromptConfigDocument cfg)
    {
        var def = PromptDefaults.CreateDefault();
        return cfg.EnableHaloPrefix == def.EnableHaloPrefix
            && cfg.EnableIdentity
            && cfg.EnableRuntime
            && cfg.EnableCmdPython
            && cfg.EnableToolGuide
            && cfg.EnableEphemeral
            && !cfg.EnableCustom
            && string.Equals(cfg.CustomPlacement, "tail", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(cfg.CustomText)
            && string.Equals(
                cfg.IdentityText,
                PromptDefaults.IdentityText,
                StringComparison.Ordinal
            )
            && string.Equals(
                cfg.RuntimeTemplate,
                PromptDefaults.RuntimeTemplate,
                StringComparison.Ordinal
            )
            && string.Equals(
                cfg.CmdPythonText,
                PromptDefaults.CmdPythonText,
                StringComparison.Ordinal
            )
            && string.Equals(
                cfg.ToolGuideTemplate,
                PromptDefaults.ToolGuideTemplate,
                StringComparison.Ordinal
            )
            && string.Equals(
                cfg.EphemeralTemplate,
                PromptDefaults.EphemeralTemplate,
                StringComparison.Ordinal
            );
    }

    private static string TryGetPythonVersion(string exePath, string launcherPrefix)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = exePath;
            string args = string.IsNullOrWhiteSpace(launcherPrefix)
                ? "--version"
                : launcherPrefix.Trim() + " --version";
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            CommandRunnerProbe.ApplyStandardEnv(process.StartInfo);
            if (!process.Start())
            {
                return string.Empty;
            }

            string stdout = process.StandardOutput.ReadToEnd() ?? string.Empty;
            string stderr = process.StandardError.ReadToEnd() ?? string.Empty;
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill();
                }
                catch
                { /* ignore */
                }
                return string.Empty;
            }

            string combined = (stdout + "\n" + stderr).Trim();
            foreach (
                string rawLine in combined.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries
                )
            )
            {
                string line = rawLine.Trim();
                if (line.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Length > 64 ? line[..64] : line;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
