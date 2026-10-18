using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
 using System.Text;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Services;

/// <summary>need06-03：提示词渲染上下文（变量替换输入）。</summary>
public sealed record PromptContext(
    string OsVersion,
    string OsArch,
    string WsName,
    string WsRoot,
    string PythonStatus,
    string Today);

/// <summary>
/// need06-03：动态系统提示词引擎（配置化重构）。
/// 唯一拼装路径 BuildCore：预览与实发共用；老 BuildAsync 保留为兼容壳（默认配置渲染）。
/// </summary>
public sealed class SystemPromptBuilder
{
    private static string? _pyCache;
    private static long _pyCacheTick;
    private static readonly object PyCacheLock = new();
    private static readonly Regex UnknownVarRegex = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    public static string GetPythonStatus()
    {
        lock (PyCacheLock)
        {
            if (_pyCache != null && Environment.TickCount64 - _pyCacheTick < 60_000)
                return _pyCache;
        }
        string display;
        try
        {
            var probe = CommandRunnerProbe.ProbePython();
            if (probe == null || !probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
            {
                display = "(Python not detected - fall back to cmd.exe)";
            }
            else
            {
                string version = TryGetPythonVersion(probe.ExePath, probe.LauncherArgsPrefix);
                display = string.IsNullOrWhiteSpace(version) ? probe.DisplayName : $"{version} ({probe.ExePath})";
            }
        }
        catch
        {
            display = "(Python not detected - fall back to cmd.exe)";
        }
        lock (PyCacheLock)
        {
            _pyCache = display;
            _pyCacheTick = Environment.TickCount64;
        }
        return display;
    }

    public static void ClearPythonCacheForTests()
    {
        lock (PyCacheLock)
        {
            _pyCache = null;
            _pyCacheTick = 0;
        }
    }

    /// <summary>兼容壳：默认配置渲染，保证老单测与老调用方逐字节一致。</summary>
    public static Task<string> BuildAsync(WorkspaceInfo workspace, string? pythonStatusOverride = null)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        var cfg = PromptDefaults.CreateDefault();
        var ctx = CreateContext(workspace, pythonStatusOverride, dateOverride: null);
        var (assembled, _, _) = BuildCore(cfg, ctx);
        return Task.FromResult(assembled);
    }

     public static PromptPreviewDto BuildPreview(
         PromptConfigDocument cfg,
         WorkspaceInfo ws,
         string? pyOverride = null,
         string? dateOverride = null,
         string? workspaceIdOverride = null,
         string? skillIndexBlock = null,
         string? memoryPinnedBlock = null,
         string? goalBlock = null)
    {
        cfg ??= PromptDefaults.CreateDefault();
         if (ws == null) throw new ArgumentNullException(nameof(ws));
         var ctx = CreateContext(ws, pyOverride, dateOverride);
         var (assembled, blocks, unknownVars) = BuildCore(cfg, ctx, skillIndexBlock, memoryPinnedBlock, goalBlock);
        bool isDefault = IsDefaultConfig(cfg);
        int total = TokenBudgetService.EstimateTokens(assembled);
        int stable = blocks.Where(b => b.Enabled && b.IsStable).Sum(b => b.EstimatedTokens);
        string hash = HeaderVariableResolver.ComputeDeterministicHash16(assembled ?? string.Empty);
        return new PromptPreviewDto
        {
            AssembledText = assembled,
            Blocks = blocks,
            TotalEstimatedTokens = total,
            StableEstimatedTokens = stable,
            PrefixHash = hash,
            WorkspaceId = workspaceIdOverride ?? ws.Id ?? string.Empty,
            UnknownVariables = unknownVars,
            IsDefault = isDefault,
            FallbackReason = null,
        };
    }

     // ============ need09-02 稳定装配线（冻结，字节级契约） ============
     /// <summary>need09-02：Skill L0 索引块标题（冻结；同时是 PrefixCachePredictor 二次切分标记）。</summary>
     public const string SkillIndexTitle = "## 5. Available Skills (auto-loaded index; read full body on demand via skill_tool)";
     /// <summary>need09-02：Skill 索引块尾部指南行（冻结）。</summary>
     public const string SkillIndexFooter = "(Use skill_tool(action=\"get\", scope=\"...\", name=\"...\") to load full body before applying.)";
     /// <summary>need09-02：Skill 超限截断行格式（冻结，{0}=被省略条数）。</summary>
     public const string SkillTruncatedLineFormat = "(…and {0} more skills omitted; list via skill_tool)";
     /// <summary>need09-02：Memory 常驻块标题（冻结；同时是二次切分标记）。</summary>
     public const string MemoryPinnedTitle = "## 6. Pinned Memory (project overrides global on same name)";
    /// <summary>need09-02：Memory 超限截断行格式（冻结，{0}=被省略条数）。</summary>
    public const string MemoryTruncatedLineFormat = "(…and {0} more memories omitted; list via memory_tool)";
    /// <summary>need13：主策略块（稳定区尾部，轮首快照渲染，关闭/超限数字动态）。</summary>
    public static string BuildSubAgentPolicyBlock(bool enabled, int maxParallel, int maxIterations, bool exec, bool file, bool skill, bool memory)
        => "# 子代理策略（本轮快照）\n"
         + $"- 可用：{(enabled ? "是" : "否")}（否=设置已关闭，禁止 spawn，若用户问并行请引导去设置开启）\n"
         + $"- 并发上限：{maxParallel}；子轮次上限：{maxIterations}；子权限：exec={(exec ? "开" : "关")} file={(file ? "开" : "关")} skill写={(skill ? "开" : "关")} memory写={(memory ? "开" : "关")} ask=禁 套娃=禁\n"
         + "- spawn 时必须把必要背景写进 task（子看不到主历史）；子以正常文本总结汇报，barrier 等全部完成才继续。";
    /// <summary>need13：子后缀块（仅 sub 追加）。</summary>
    public const string SubAgentSuffixBlock =
        "# 子代理约束\n"
        + "- 你是寄宿于主会话的子代理，会话干净，无主历史。\n"
        + "- 禁止修改 skill/memory（只有 list/get 可用）；禁止 ask_user（按保守默认继续并在总结注明假设）；禁止再 spawn 子代理。\n"
        + "- 结束时必须输出一段自然语言总结：结论/变更文件/验证方式/未完成与风险。";
     /// <summary>need09-02：toolGuide 追加的 Skill/Memory/Images 指南 3 行（冻结英文，永不改）。</summary>
     public const string SkillMemoryGuideSuffix =
         "- Skills: consult `## 5. Available Skills` index first; load body via `skill_tool` before applying; project scope overrides global on same name.\n" +
         "- Memory: `## 6. Pinned Memory` is authoritative; use `memory_tool` to list/read archived memory; never invent persistent facts.\n" +
         "- Images: use `view_image` to inspect workspace images; user-pasted images arrive inline in the latest message.";
     /// <summary>need09-02：L0 Skill 索引常驻配额（tokens）。</summary>
     public const int SkillIndexTokenBudget = 1500;
     /// <summary>need09-02：常驻 Memory 配额（tokens）。</summary>
     public const int MemoryPinnedTokenBudget = 2000;
     /// <summary>need09-02：索引 summary 上限（字符数，超限截断+…）。</summary>
     public const int SkillSummaryMaxChars = 60;

     /// <summary>need09-02：L0 索引条目（解耦 DTO：只用原始类型，不引用 SkillMemoryService 任何类型）。</summary>
     public sealed record StableSkillEntry(string Scope, string Name, string Summary, int Priority);

     /// <summary>need09-02：常驻记忆条目（解耦 DTO：只用原始类型）。</summary>
     public sealed record StableMemoryEntry(string Scope, string Name, string Body, int Priority);

     /// <summary>need09-02：块体规范化：\r\n→\n + TrimEnd（幂等；无 \r 输入时与老 TrimEnd 逐字节一致）。</summary>
     public static string NormalizeBlockBody(string? body)
         => (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd();

     /// <summary>need09-02：summary 规范化：Trim + \n→空格 + 超 60 字截断+…（冻结；按 Unicode 标量计数，防代理对切断）。</summary>
     public static string NormalizeSkillSummary(string? summary, int maxChars = SkillSummaryMaxChars)
     {
         string s = (summary ?? string.Empty).Trim().Replace("\n", " ");
         if (s.Length == 0) return string.Empty;
         int runes = 0;
         foreach (var r in s.EnumerateRunes()) runes++;
         if (runes <= maxChars) return s;
         return string.Concat(s.EnumerateRunes().Take(maxChars)) + "…";
     }

     internal static int StableScopeRank(string? scope)
         => string.Equals(scope?.Trim(), "global", StringComparison.OrdinalIgnoreCase) ? 0
             : string.Equals(scope?.Trim(), "project", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

     /// <summary>
     /// need09-02：渲染 L0 Skill 索引块（空域返回 string.Empty 由 BuildCore 整块丢弃）。
     /// 行格式冻结：`- [&lt;scope&gt;] &lt;name&gt; — &lt;summary≤60字&gt;`；排序 scope(global先)→name升序（与 priority 无关）；
     /// 超限时按 priority 高保留 + name 升序截断并追加冻结截断行，渲染顺序仍保持稳定排序。
     /// </summary>
     public static string BuildSkillIndexBlock(IEnumerable<StableSkillEntry>? entries, int tokenBudget = SkillIndexTokenBudget)
     {
         var list = (entries ?? Enumerable.Empty<StableSkillEntry>())
             .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
             .Select(e => new StableSkillEntry(
                 string.IsNullOrWhiteSpace(e.Scope) ? "global" : e.Scope.Trim(),
                 e.Name.Trim(),
                 NormalizeSkillSummary(e.Summary),
                 e.Priority))
             .ToList();
         if (list.Count == 0) return string.Empty;
         List<StableSkillEntry> StableOrder(IEnumerable<StableSkillEntry> src)
             => src.OrderBy(e => StableScopeRank(e.Scope)).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
         string RenderRow(StableSkillEntry e) => ("- [" + e.Scope + "] " + e.Name + " — " + e.Summary).TrimEnd();
         string Render(IReadOnlyList<StableSkillEntry> rows, int omitted)
         {
             var lines = new List<string>(rows.Count + 3) { SkillIndexTitle };
             foreach (var r in rows) lines.Add(RenderRow(r));
             if (omitted > 0) lines.Add(string.Format(SkillTruncatedLineFormat, omitted));
             lines.Add(SkillIndexFooter);
             return string.Join("\n", lines);
         }
         var stable = StableOrder(list);
         if (TokenBudgetService.EstimateTokens(Render(stable, 0)) <= tokenBudget)
             return Render(stable, 0);
         var byPriority = list.OrderByDescending(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
         var kept = new List<StableSkillEntry>();
         foreach (var e in byPriority)
         {
             var trial = new List<StableSkillEntry>(kept) { e };
             if (TokenBudgetService.EstimateTokens(Render(StableOrder(trial), list.Count - trial.Count)) <= tokenBudget)
                 kept = trial;
         }
         return Render(StableOrder(kept), list.Count - kept.Count);
     }

     /// <summary>
     /// need09-02：渲染常驻 Memory 块（空域返回 string.Empty 由 BuildCore 整块丢弃）。
     /// 同名去重：project 覆盖 global（整条替换）；排序按 name 升序（与 priority 无关）；
     /// 超限截断同 Skill（高 priority 保留 + 冻结截断行）。
     /// </summary>
     public static string BuildMemoryPinnedBlock(IEnumerable<StableMemoryEntry>? entries, int tokenBudget = MemoryPinnedTokenBudget)
     {
         var list = (entries ?? Enumerable.Empty<StableMemoryEntry>())
             .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
             .Select(e => new StableMemoryEntry(
                 string.IsNullOrWhiteSpace(e.Scope) ? "global" : e.Scope.Trim(),
                 e.Name.Trim(),
                 NormalizeBlockBody(e.Body),
                 e.Priority))
             .Where(e => !string.IsNullOrEmpty(e.Body))
             .ToList();
         if (list.Count == 0) return string.Empty;
         var deduped = list.GroupBy(e => e.Name, StringComparer.Ordinal)
             .Select(g => g
                 .OrderBy(e => string.Equals(e.Scope, "project", StringComparison.OrdinalIgnoreCase) ? 0
                     : string.Equals(e.Scope, "global", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                 .ThenByDescending(e => e.Priority)
                 .First())
             .ToList();
         List<StableMemoryEntry> StableOrder(IEnumerable<StableMemoryEntry> src)
             => src.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
         string Render(IReadOnlyList<StableMemoryEntry> rows, int omitted)
         {
             var lines = new List<string>(rows.Count * 2 + 2) { MemoryPinnedTitle };
             foreach (var r in rows)
             {
                 lines.Add("### [" + r.Scope + "] " + r.Name);
                 lines.Add(r.Body);
             }
             if (omitted > 0) lines.Add(string.Format(MemoryTruncatedLineFormat, omitted));
             return string.Join("\n", lines);
         }
         var stable = StableOrder(deduped);
         if (TokenBudgetService.EstimateTokens(Render(stable, 0)) <= tokenBudget)
             return Render(stable, 0);
         var byPriority = deduped.OrderByDescending(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
         var kept = new List<StableMemoryEntry>();
         foreach (var e in byPriority)
         {
             var trial = new List<StableMemoryEntry>(kept) { e };
             if (TokenBudgetService.EstimateTokens(Render(StableOrder(trial), deduped.Count - trial.Count)) <= tokenBudget)
                 kept = trial;
         }
         return Render(StableOrder(kept), deduped.Count - kept.Count);
     }
     /// <summary>need16：渲染 Session Goal 块（空/无内容返回 string.Empty 由 BuildCore 整块丢弃，stable=true）。</summary>
     public static string BuildGoalBlock(Host.Desktop.Core.Models.SessionGoal? goal)
     {
         if (goal == null || !goal.HasContent) return string.Empty;
         var ms = goal.Milestones ?? new List<Host.Desktop.Core.Models.GoalMilestone>();
         int total = ms.Count; int done = ms.Count(m => m.Done);
         var lines = new List<string>();
         lines.Add("## 5. Session Goal（本轮目标，goal_tool 可改）");
         if (!string.IsNullOrWhiteSpace(goal.Title)) lines.Add("- 标题：" + goal.Title.Trim());
         if (!string.IsNullOrWhiteSpace(goal.Description)) lines.Add("- 描述：" + goal.Description.Trim());
         lines.Add($"- 进度：{goal.Percent}%（{done}/{total} 里程碑）" + (string.IsNullOrWhiteSpace(goal.Note) ? string.Empty : goal.Note.Trim()));
         int show = 0;
         foreach (var m in ms)
         {
             if (show >= 20) break;
             lines.Add("- [" + (m.Done ? "x" : " ") + "] " + (m.Title ?? string.Empty).Trim());
             show++;
         }
         if (total > show) lines.Add($"…余{total - show}条");
         lines.Add("Goal 变更经 goal_tool 下一轮生效；子会话只读。");
         return string.Join("\n", lines);
     }

     /// <summary>
     /// need09-02：唯一拼装路径。装配顺序（冻结）：custom(head) → identity → runtime → cmdPython → toolGuide →
     /// skillIndex → memoryPinned → ephemeral → custom(tail)。skill/memory 为渲染好的 string 块（B 轨道不引用
     /// SkillMemoryService 类型）；null/空/空白视为空域，整块（含标题）直接消失；缺省即老输出逐字节一致。
     /// </summary>
     public static (string assembled, List<PromptBlockPreview> blocks, List<string> unknownVars) BuildCore(
         PromptConfigDocument cfg, PromptContext ctx, string? skillIndexBlock = null, string? memoryPinnedBlock = null, string? goalBlock = null)
     {
         var blocks = new List<PromptBlockPreview>();
         var unknown = new List<string>();
         if (!cfg.EnableHaloPrefix)
             return (string.Empty, blocks, unknown);

         var ordered = new List<(string id, string text, bool stable, bool enabled)>();
         string customPlacement = string.Equals(cfg.CustomPlacement, "head", StringComparison.OrdinalIgnoreCase) ? "head" : "tail";
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
             if (!string.IsNullOrWhiteSpace(rt)) ordered.Add(("runtime", rt, true, true));
         }
         if (cfg.EnableCmdPython && !string.IsNullOrWhiteSpace(cfg.CmdPythonText))
             ordered.Add(("cmdPython", (cfg.CmdPythonText ?? string.Empty).TrimEnd(), true, true));
         if (cfg.EnableToolGuide)
         {
             string tg = RenderTemplate(cfg.ToolGuideTemplate ?? string.Empty, ctx, unknown).TrimEnd();
             if (!string.IsNullOrWhiteSpace(tg))
             {
                 tg = NormalizeBlockBody(tg) + "\n" + SkillMemoryGuideSuffix;
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
             string eph = RenderTemplate(cfg.EphemeralTemplate ?? string.Empty, ctx, unknown).TrimEnd();
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
             blocks.Add(new PromptBlockPreview
             {
                 BlockId = id,
                 Enabled = true,
                 RenderedText = text,
                 EstimatedTokens = TokenBudgetService.EstimateTokens(text),
                 IsStable = stable,
             });
         }

         // 关断块也记录（Enabled=false），满足 T2 断言 Blocks 全 Disabled
         if (blocks.Count == 0 && cfg.EnableHaloPrefix)
         {
             foreach (string id in new[] { "identity", "runtime", "cmdPython", "toolGuide", "skillIndex", "memoryPinned", "ephemeral", "custom" })
                 blocks.Add(new PromptBlockPreview { BlockId = id, Enabled = false, RenderedText = string.Empty, EstimatedTokens = 0, IsStable = !string.Equals(id, "ephemeral", StringComparison.Ordinal) });
             return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
         }
         if (!cfg.EnableHaloPrefix)
             return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());

         string assembled = ordered.Count == 0
             ? string.Empty
             : string.Join("\n\n", ordered.Select(p => NormalizeBlockBody(p.text))) + "\n";
         return (assembled, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
     }

    private static PromptContext CreateContext(WorkspaceInfo ws, string? pyOverride, string? dateOverride)
    {
        string osVersion = RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.VersionString;
        string arch = RuntimeInformation.OSArchitecture.ToString();
        string wsName = string.IsNullOrWhiteSpace(ws.Name) ? "workspace" : ws.Name.Trim();
        string wsRoot = string.IsNullOrWhiteSpace(ws.RootPath) ? Environment.CurrentDirectory : ws.RootPath;
        string pythonStatus = string.IsNullOrWhiteSpace(pyOverride) ? GetPythonStatus() : pyOverride.Trim();
        string today = string.IsNullOrWhiteSpace(dateOverride) ? DateTime.Now.ToString("yyyy-MM-dd") : dateOverride.Trim();
        return new PromptContext(osVersion, arch, wsName, wsRoot, pythonStatus, today);
    }

    internal static string RenderTemplate(string template, PromptContext ctx, List<string>? unknownSink = null)
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
            && cfg.EnableIdentity && cfg.EnableRuntime && cfg.EnableCmdPython && cfg.EnableToolGuide && cfg.EnableEphemeral
            && !cfg.EnableCustom
            && string.Equals(cfg.CustomPlacement, "tail", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(cfg.CustomText)
            && string.Equals(cfg.IdentityText, PromptDefaults.IdentityText, StringComparison.Ordinal)
            && string.Equals(cfg.RuntimeTemplate, PromptDefaults.RuntimeTemplate, StringComparison.Ordinal)
            && string.Equals(cfg.CmdPythonText, PromptDefaults.CmdPythonText, StringComparison.Ordinal)
            && string.Equals(cfg.ToolGuideTemplate, PromptDefaults.ToolGuideTemplate, StringComparison.Ordinal)
            && string.Equals(cfg.EphemeralTemplate, PromptDefaults.EphemeralTemplate, StringComparison.Ordinal);
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
                try { process.Kill(); } catch { /* ignore */ }
                return string.Empty;
            }

            string combined = (stdout + "\n" + stderr).Trim();
            foreach (string rawLine in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
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
