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

/// <summary>
/// need06-03：动态系统提示词引擎（配置化重构）。
/// 唯一拼装路径 BuildCore：预览与实发共用；老 BuildAsync 保留为兼容壳（默认配置渲染）。
/// </summary>
public sealed partial class SystemPromptBuilder
{
    // need17-07：60s 缓存逻辑已删除，残留字段仅为兼容 ClearPythonCacheForTests（历史测试入口），对外行为不变。
#pragma warning disable CS0414 // 历史残留字段：有意保留，仅写不读
    private static string? _pyCache;
    private static long _pyCacheTick;
#pragma warning restore CS0414
    private static readonly object PyCacheLock = new();
    private static readonly Regex UnknownVarRegex =
        new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    // need17-07：收敛读快照（覆盖注入时读到注入值；60s 缓存逻辑删除，对外行为不变）
    public static string GetPythonStatus()
    {
        var snap = Host.Desktop.Tools.Process.PythonAvailabilitySnapshot.Current;
        if (snap == null || !snap.Found || string.IsNullOrWhiteSpace(snap.ExePath))
            return "(Python not detected - fall back to cmd.exe)";
        string version = TryGetPythonVersion(snap.ExePath, string.Empty);
        return string.IsNullOrWhiteSpace(version)
            ? (snap.DisplayName ?? snap.ExePath)
            : $"{version} ({snap.ExePath})";
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
    public static Task<string> BuildAsync(
        WorkspaceInfo workspace,
        string? pythonStatusOverride = null
    )
    {
        if (workspace == null)
            throw new ArgumentNullException(nameof(workspace));
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
        string? goalBlock = null
    )
    {
        cfg ??= PromptDefaults.CreateDefault();
        if (ws == null)
            throw new ArgumentNullException(nameof(ws));
        var ctx = CreateContext(ws, pyOverride, dateOverride);
        var (assembled, blocks, unknownVars) = BuildCore(
            cfg,
            ctx,
            skillIndexBlock,
            memoryPinnedBlock,
            goalBlock
        );
        bool isDefault = IsDefaultConfig(cfg);
        int total = TokenBudgetService.EstimateTokens(assembled);
        int stable = blocks.Where(b => b.Enabled && b.IsStable).Sum(b => b.EstimatedTokens);
        string hash = HeaderVariableResolver.ComputeDeterministicHash16(assembled ?? string.Empty);
        return new PromptPreviewDto
        {
            AssembledText = assembled ?? string.Empty,
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
    public const string SkillIndexTitle =
        "## 5. Available Skills (auto-loaded index; read full body on demand via skill_tool)";

    /// <summary>need09-02：Skill 索引块尾部指南行（冻结）。</summary>
    public const string SkillIndexFooter =
        "(Use skill_tool(action=\"get\", scope=\"...\", name=\"...\") to load full body before applying.)";

    /// <summary>need09-02：Skill 超限截断行格式（冻结，{0}=被省略条数）。</summary>
    public const string SkillTruncatedLineFormat =
        "(…and {0} more skills omitted; list via skill_tool)";

    /// <summary>need09-02：Memory 常驻块标题（冻结；同时是二次切分标记）。</summary>
    public const string MemoryPinnedTitle =
        "## 6. Pinned Memory (project overrides global on same name)";

    /// <summary>need09-02：Memory 超限截断行格式（冻结，{0}=被省略条数）。</summary>
    public const string MemoryTruncatedLineFormat =
        "(…and {0} more memories omitted; list via memory_tool)";

    /// <summary>need13：主策略块（稳定区尾部，轮首快照渲染，关闭/超限数字动态）。</summary>
    public static string BuildSubAgentPolicyBlock(
        bool enabled,
        int maxParallel,
        int maxIterations,
        bool exec,
        bool file,
        bool skill,
        bool memory
    ) =>
        "# 子代理策略（本轮快照）\n"
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
        "- Skills: consult `## 5. Available Skills` index first; load body via `skill_tool` before applying; project scope overrides global on same name.\n"
        + "- Memory: `## 6. Pinned Memory` is authoritative; use `memory_tool` to list/read archived memory; never invent persistent facts.\n"
        + "- Images: use `view_image` to inspect workspace images; user-pasted images arrive inline in the latest message.";

    // ============ need17-05/06 动态工具段 + 规则段（冻结顺序：通用纪律 → 工具段 → 规则段） ============
    // NEED17-01 接入后：brief/rules 改从 ToolDefinition.PromptBrief/PromptRules 读取，本表仅作回退（core/search 行待 03/04 分册认领）。
    private sealed record Need17ToolEntry(string Name, string Brief, string[] Rules);

    private static readonly Need17ToolEntry[] Need17ToolTable =
    {
        new(
            "read_file",
            "Read file content (paged); always read before editing",
            Array.Empty<string>()
        ),
        new(
            "exec_command",
            "Run shell commands in the workspace root (cmd first)",
            Array.Empty<string>()
        ),
        new("edit_file", "Small partial edit with a verbatim string anchor", Array.Empty<string>()),
        new("edit_lines", "Line-number edit (explicit limit; 0 = insert)", Array.Empty<string>()),
        new(
            "write_file",
            "Create new files or fully overwrite (allow_overwrite:true)",
            Array.Empty<string>()
        ),
        new("list_dir", "List directory structure", Array.Empty<string>()),
        new("grep_search", "Search file content", Array.Empty<string>()),
        new("view_image", "Inspect workspace images", Array.Empty<string>()),
        new("undo_edit", "Undo the last file edit", Array.Empty<string>()),
        // NEED17-05 §5.3 overlay 行（与各工具 PromptBrief/PromptRules 常量同文）：
        new(
            "ask_user",
            Host.Desktop.Tools.BuiltIn.AskUserTool.PromptBrief,
            Host.Desktop.Tools.BuiltIn.AskUserTool.PromptRules
        ),
        new(
            "skill_tool",
            Host.Desktop.Tools.BuiltIn.SkillTool.PromptBrief,
            Host.Desktop.Tools.BuiltIn.SkillTool.PromptRules
        ),
        new(
            "memory_tool",
            Host.Desktop.Tools.BuiltIn.MemoryTool.PromptBrief,
            Host.Desktop.Tools.BuiltIn.MemoryTool.PromptRules
        ),
        new(
            "spawn_subagent",
            Host.Desktop.Tools.BuiltIn.SpawnSubAgentTool.PromptBrief,
            Host.Desktop.Tools.BuiltIn.SpawnSubAgentTool.PromptRules
        ),
        new(
            "goal_tool",
            Host.Desktop.Tools.BuiltIn.GoalTool.PromptBrief,
            Host.Desktop.Tools.BuiltIn.GoalTool.PromptRules
        ),
    };

    /// <summary>need17-06 §6.2：隐藏声明工具（规则同步剔除；06 规则不再提 edit）。</summary>
    public static readonly ISet<string> Need17HiddenTools = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "edit"
    };

    /// <summary>need17-01/05：Coding 预设（工具段恰 5 行，供验收与 orchestrator 传入）。</summary>
    public static readonly ISet<string> Need17CodingPreset = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "read_file",
        "exec_command",
        "edit_file",
        "edit_lines",
        "write_file"
    };

    /// <summary>need17-06 §6.2：exec 有但 grep/list 任一无时的条件规则行（抄 pi 10.3 §38）。</summary>
    public const string Need17ExecFallbackRule =
        "Use exec_command (dir/findstr) for file listing and searching.";

    /// <summary>need17-06 §6.2：工具段（## Available Tools + 逐行 - name: brief；无 brief 省略；空集写 none）。</summary>
    public static string BuildAvailableToolsSection(ISet<string>? activeToolNames)
    {
        var rows = new List<string>();
        foreach (var e in Need17ToolTable)
        {
            if (Need17HiddenTools.Contains(e.Name))
                continue;
            if (activeToolNames != null && !activeToolNames.Contains(e.Name))
                continue;
            if (string.IsNullOrWhiteSpace(e.Brief))
                continue;
            rows.Add("- " + e.Name + ": " + e.Brief.Trim());
        }
        string body = rows.Count == 0 ? "none" : string.Join("\n", rows);
        return "## Available Tools\n" + body;
    }

    /// <summary>need17-06 §6.2：规则段（激活工具 PromptRules 去重拼接；隐藏工具规则同步剔除）。</summary>
    public static string BuildActiveRulesSection(ISet<string>? activeToolNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<string>();
        void Add(string? r)
        {
            string t = (r ?? string.Empty).Trim();
            if (t.Length == 0 || !seen.Add(t))
                return;
            rules.Add("- " + t);
        }
        foreach (var e in Need17ToolTable)
        {
            if (Need17HiddenTools.Contains(e.Name))
                continue;
            if (activeToolNames != null && !activeToolNames.Contains(e.Name))
                continue;
            if (e.Rules == null)
                continue;
            foreach (var r in e.Rules)
                Add(r);
        }
        bool hasExec = Need17ToolTable.Any(e =>
            e.Name == "exec_command"
            && (activeToolNames == null || activeToolNames.Contains(e.Name))
        );
        bool hasGrep = activeToolNames == null || activeToolNames.Contains("grep_search");
        bool hasList = activeToolNames == null || activeToolNames.Contains("list_dir");
        if (hasExec && !hasGrep && !hasList)
            Add(Need17ExecFallbackRule);
        if (rules.Count == 0)
            return string.Empty;
        return "## Tool Rules\n" + string.Join("\n", rules);
    }

    private static string BuildNeed17DynamicSuffix(ISet<string>? activeToolNames)
    {
        string tools = BuildAvailableToolsSection(activeToolNames);
        string rules = BuildActiveRulesSection(activeToolNames);
        string suffix = "\n" + tools;
        if (!string.IsNullOrEmpty(rules))
            suffix += "\n" + rules;
        return suffix;
    }

    /// <summary>need09-02：L0 Skill 索引常驻配额（tokens）。</summary>
    public const int SkillIndexTokenBudget = 1500;

    /// <summary>need09-02：常驻 Memory 配额（tokens）。</summary>
    public const int MemoryPinnedTokenBudget = 2000;

    /// <summary>need09-02：索引 summary 上限（字符数，超限截断+…）。</summary>
    public const int SkillSummaryMaxChars = 60;
}
