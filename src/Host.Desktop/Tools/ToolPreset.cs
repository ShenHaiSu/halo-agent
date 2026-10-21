using Host.Desktop.Core.Models;

namespace Host.Desktop.Tools;

/// <summary>
/// need17-01：工具激活集 preset（照抄 pi 10.6 语义）。
/// Coding=默认编码包；ReadOnly=只读包；Full=全部 Direct 工具（overlay 仅 Full 或显式点名）。
/// 解析规则：纯名单=替换继承；纯加减（+x/-y）=增量；混合纯名与加减=非法（启动阻断，中文文案）。
/// </summary>
public static class ToolPreset
{
    public const string CodingName = "coding";
    public const string ReadOnlyName = "readonly";
    public const string FullName = "full";

    public static readonly string[] Coding =
    {
        "read_file",
        "exec_command",
        "edit_file",
        "edit_lines",
        "write_file"
    };
    public static readonly string[] ReadOnly =
    {
        "read_file",
        "list_dir",
        "grep_search",
        "view_image"
    };

    /// <summary>need17-01/05：叠加工具（overlay），仅 Full 或显式点名才声明。</summary>
    public static readonly string[] Overlay =
    {
        "ask_user",
        "skill_tool",
        "memory_tool",
        "spawn_subagent",
        "goal_tool"
    };

    public static bool IsOverlay(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Overlay.Contains(name!.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// need17-01：校验 preset/allowlist/denylist 互斥规则，非法抛 AgentConfigValidationException（中文文案，启动阻断）。
    /// 规则：① preset 与纯名单 allowlist 不可同时指定；② 同一名单内不可混合纯名与 +/- 加减。
    /// </summary>
    public static void ValidateOrThrow(
        string? preset,
        List<string>? allowlist,
        List<string>? denylist
    )
    {
        string? p = string.IsNullOrWhiteSpace(preset) ? null : preset!.Trim();
        if (p != null && !IsKnownPreset(p))
        {
            throw new AgentConfigValidationException(
                "toolsPreset",
                $"工具预设非法：'{p}'（可选值：coding | readonly | full）。请修正 agent.json 后重试。"
            );
        }

        bool allowHasBare = HasBareName(allowlist);
        bool allowHasDelta = HasDeltaEntry(allowlist);
        if (allowHasBare && allowHasDelta)
        {
            throw new AgentConfigValidationException(
                "toolsAllowlist",
                "工具白名单不可混合纯名单与加减项（如同时出现 'grep_search' 与 '+grep_search'）。请只用其中一种写法后重试。"
            );
        }

        bool denyHasBare = HasBareName(denylist);
        bool denyHasDelta = HasDeltaEntry(denylist);
        if (denyHasBare && denyHasDelta)
        {
            throw new AgentConfigValidationException(
                "toolsDenylist",
                "工具黑名单不可混合纯名单与加减项（如同时出现 'spawn_subagent' 与 '-spawn_subagent'）。请只用其中一种写法后重试。"
            );
        }

        if (p != null && allowHasBare)
        {
            throw new AgentConfigValidationException(
                "toolsAllowlist",
                $"工具预设 toolsPreset='{p}' 与纯名单 toolsAllowlist 不可同时指定（二者均为替换语义）。请只保留其一后重试。"
            );
        }
    }

    /// <summary>
    /// need17-01：按配置解析本轮激活工具名集合（Orchestrator 轮首计算一次，本轮冻结；主 Agent 联调点见 L375）。
    /// 默认 preset=Coding。overlay 是否叠加由调用方按 SubPolicy 决定（resolve 时不自动加 overlay，除 Full 外）。
    /// </summary>
    public static ISet<string> ResolveActiveSet(
        IEnumerable<string> registeredNames,
        AgentConfigDocument? cfg
    )
    {
        var registered = new HashSet<string>(
            (registeredNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase
        );

        string preset = string.IsNullOrWhiteSpace(cfg?.ToolsPreset)
            ? CodingName
            : cfg!.ToolsPreset!.Trim().ToLowerInvariant();
        HashSet<string> active = preset switch
        {
            ReadOnlyName => new HashSet<string>(ReadOnly, StringComparer.OrdinalIgnoreCase),
            FullName => new HashSet<string>(registered, StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(Coding, StringComparer.OrdinalIgnoreCase),
        };

        ApplyList(active, registered, cfg?.ToolsAllowlist, isAllowlist: true);
        ApplyList(active, registered, cfg?.ToolsDenylist, isAllowlist: false);
        active.IntersectWith(registered);
        return active;
    }

    private static void ApplyList(
        HashSet<string> active,
        HashSet<string> registered,
        List<string>? list,
        bool isAllowlist
    )
    {
        if (list == null || list.Count == 0)
        {
            return;
        }

        var bare = list.Where(e =>
                !string.IsNullOrWhiteSpace(e)
                && !e.Trim().StartsWith("+")
                && !e.Trim().StartsWith("-")
            )
            .Select(e => e.Trim())
            .ToList();
        var deltas = list.Where(e =>
                !string.IsNullOrWhiteSpace(e)
                && (e.Trim().StartsWith("+") || e.Trim().StartsWith("-"))
            )
            .Select(e => e.Trim())
            .ToList();

        if (bare.Count > 0)
        {
            if (isAllowlist)
            {
                // 纯名单=替换继承。
                active.Clear();
                foreach (var n in bare)
                {
                    active.Add(n);
                }
            }
            else
            {
                // denylist 纯名单=从当前集移除。
                foreach (var n in bare)
                {
                    active.Remove(n);
                }
            }
        }

        // 纯加减=增量。
        foreach (var d in deltas)
        {
            bool add = d.StartsWith("+");
            string n = d.Substring(1).Trim();
            if (string.IsNullOrEmpty(n))
            {
                continue;
            }
            if (add)
            {
                active.Add(n);
            }
            else
            {
                active.Remove(n);
            }
        }
    }

    private static bool IsKnownPreset(string p) =>
        p.Equals(CodingName, StringComparison.OrdinalIgnoreCase)
        || p.Equals(ReadOnlyName, StringComparison.OrdinalIgnoreCase)
        || p.Equals(FullName, StringComparison.OrdinalIgnoreCase);

    private static bool HasBareName(List<string>? list) =>
        list != null
        && list.Any(e =>
            !string.IsNullOrWhiteSpace(e) && !e.Trim().StartsWith("+") && !e.Trim().StartsWith("-")
        );

    private static bool HasDeltaEntry(List<string>? list) =>
        list != null
        && list.Any(e =>
            !string.IsNullOrWhiteSpace(e) && (e.Trim().StartsWith("+") || e.Trim().StartsWith("-"))
        );
}
