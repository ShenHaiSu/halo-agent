using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// 权限判定：模式 × 操作 × 风险 → 决策。
///
/// ============ 这是唯一的决策点 ============
/// 不要在别处再写一份"这个模式能不能写文件"。所有调用方都用 Check()。
///
/// ============ 20 格矩阵（与文档一致） ============
///              写文件(交互)  写文件(无头)  命令(交互)  命令(无头)  高危
/// ask             Ask           Deny         Ask         Deny      AskNoAlways
/// acceptEdits   Allow         Allow          Ask         Deny      AskNoAlways
/// bypass        Allow         Allow        Allow        Allow      AskNoAlways/Deny
/// yolo          Allow         Allow        Allow        Allow      Allow
/// </summary>
public static class PermissionChecker
{
    public enum Operation { ReadFile, WriteFile, RunCommand }

    public static PermDecision Check(
        PermissionMode mode,
        Operation op,
        CommandRisk? risk,
        bool interactive)
    {
        // ---------- YOLO：一切放行 ----------
        if (mode == PermissionMode.Yolo) return PermDecision.Allow;

        risk ??= CommandRisk.Safe;

        // ---------- 高危命令：任何模式都要人点头（YOLO 除外，已在上方放行） ----------
        if (risk.Kind == CommandRiskKind.Blocked)
        {
            if (!interactive)
                return PermDecision.Deny(string.Format(PermissionPolicies.BlockedHeadless, risk.Describe()));

            return PermDecision.NeedAsk(
                title: "命令执行确认",
                body: risk.Describe(),
                warning: risk.Describe(),
                allowAlways: false);
        }

        // ---------- 无法判定：要人点头，且不给 always ----------
        if (risk.Kind == CommandRiskKind.Uncertain)
        {
            if (!interactive)
            {
                // bypassPermissions 在无头模式放行 Uncertain（否则内网批量任务寸步难行）
                return mode == PermissionMode.BypassPermissions
                    ? PermDecision.Allow
                    : PermDecision.Deny(string.Format(PermissionPolicies.UncertainHeadless, risk.Describe()));
            }

            return PermDecision.NeedAsk(
                title: "命令执行确认",
                body: "命令目标无法确定，请人工判断",
                warning: risk.Describe(),
                allowAlways: false);
        }

        // ---------- 普通操作 ----------
        return op switch
        {
            Operation.ReadFile => PermDecision.Allow,

            Operation.WriteFile => mode switch
            {
                PermissionMode.Ask => interactive
                    ? PermDecision.NeedAsk("文件写入确认", "HaloAgent 想要修改文件", null, allowAlways: true)
                    : PermDecision.Deny(PermissionPolicies.NeedAcceptEdits),
                _ => PermDecision.Allow,
            },

            Operation.RunCommand => mode switch
            {
                PermissionMode.Ask or PermissionMode.AcceptEdits => interactive
                    ? PermDecision.NeedAsk("命令执行确认", "", null, allowAlways: true)
                    : PermDecision.Deny(PermissionPolicies.NeedBypass),
                _ => PermDecision.Allow,
            },

            _ => PermDecision.Deny("未知操作"),
        };
    }

    /// <summary>
    /// 快捷：判断某个模式是否会在**无头模式**下直接拒绝该操作。
    /// 供 Engine.Cli 启动时预检、以及前端禁用某些按钮用。
    /// </summary>
    public static bool WouldDenyHeadless(PermissionMode mode, Operation op) =>
        Check(mode, op, CommandRisk.Safe, interactive: false).Kind == PermDecisionKind.Deny;
}
