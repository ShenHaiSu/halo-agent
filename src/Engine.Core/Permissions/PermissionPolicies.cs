namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// 提示文案常量。**只此一份** —— 提示与行为必须对齐。
/// </summary>
public static class PermissionPolicies
{
    public const string NeedAcceptEdits =
        "ask 模式下写文件要确认，无头模式直接拒绝。要自动改文件就加 --permission acceptEdits";

    public const string NeedBypass =
        "该模式下跑命令要确认，无头模式直接拒绝。要自动执行就加 --permission bypassPermissions";

    public const string BlockedHeadless =
        "命令被安全策略拦截：{0}。无头模式无法交互确认，已拒绝" +
        "（bypassPermissions 也不放行高危命令）；如确需执行，请自行在终端跑";

    public const string UncertainHeadless =
        "命令目标无法确定（{0}），无头模式已拒绝。要自动执行就加 --permission bypassPermissions";

    public const string UserDenied = "用户拒绝了该操作";
    public const string AskChannelClosed = "确认通道已关闭";
    public const string AskInterrupted = "用户中断了确认";
}
