using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Permissions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Permissions;

/// <summary>
/// 权限矩阵测试：4 档 × 3 类 × 交互/无头。
/// </summary>
public class PermissionCheckerTests
{
    private static PermDecisionKind C(PermissionMode m, PermissionChecker.Operation op,
        bool interactive, CommandRisk? risk = null) =>
        PermissionChecker.Check(m, op, risk, interactive).Kind;

    // ---------- ask ----------
    [Fact]
    public void Ask_interactive_asks_for_write_and_command()
    {
        Assert.Equal(PermDecisionKind.NeedAsk,
            C(PermissionMode.Ask, PermissionChecker.Operation.WriteFile, true));
        Assert.Equal(PermDecisionKind.NeedAsk,
            C(PermissionMode.Ask, PermissionChecker.Operation.RunCommand, true));
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.Ask, PermissionChecker.Operation.ReadFile, true));
    }

    [Fact]
    public void Ask_headless_denies_write_and_command()
    {
        Assert.Equal(PermDecisionKind.Deny,
            C(PermissionMode.Ask, PermissionChecker.Operation.WriteFile, false));
        Assert.Equal(PermDecisionKind.Deny,
            C(PermissionMode.Ask, PermissionChecker.Operation.RunCommand, false));
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.Ask, PermissionChecker.Operation.ReadFile, false));
    }

    // ---------- acceptEdits ----------
    [Fact]
    public void AcceptEdits_allows_write_but_asks_command()
    {
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.AcceptEdits, PermissionChecker.Operation.WriteFile, true));
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.AcceptEdits, PermissionChecker.Operation.WriteFile, false));
        Assert.Equal(PermDecisionKind.NeedAsk,
            C(PermissionMode.AcceptEdits, PermissionChecker.Operation.RunCommand, true));
        Assert.Equal(PermDecisionKind.Deny,
            C(PermissionMode.AcceptEdits, PermissionChecker.Operation.RunCommand, false));
    }

    // ---------- bypassPermissions ----------
    [Fact]
    public void Bypass_allows_normal_but_asks_blocked()
    {
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.BypassPermissions, PermissionChecker.Operation.RunCommand, true));
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.BypassPermissions, PermissionChecker.Operation.WriteFile, false));

        // 高危：交互仍要问
        Assert.Equal(PermDecisionKind.NeedAsk,
            C(PermissionMode.BypassPermissions, PermissionChecker.Operation.RunCommand, true,
                CommandRisk.Blocked("根目录", "/")));
    }

    [Fact]
    public void Bypass_headless_allows_uncertain_but_denies_blocked()
    {
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.BypassPermissions, PermissionChecker.Operation.RunCommand, false,
                CommandRisk.Uncertain("含变量", "$X")));

        Assert.Equal(PermDecisionKind.Deny,
            C(PermissionMode.BypassPermissions, PermissionChecker.Operation.RunCommand, false,
                CommandRisk.Blocked("根目录", "/")));
    }

    // ---------- yolo ----------
    [Fact]
    public void Yolo_allows_everything_including_blocked()
    {
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.Yolo, PermissionChecker.Operation.RunCommand, true,
                CommandRisk.Blocked("根目录", "/")));
        Assert.Equal(PermDecisionKind.Allow,
            C(PermissionMode.Yolo, PermissionChecker.Operation.WriteFile, false));
    }

    // ---------- warning 降级 always ----------
    [Fact]
    public void Warning_forces_allowAlways_false()
    {
        var d = PermissionChecker.Check(
            PermissionMode.BypassPermissions,
            PermissionChecker.Operation.RunCommand,
            CommandRisk.Blocked("根目录", "/"),
            interactive: true);

        Assert.Equal(PermDecisionKind.NeedAsk, d.Kind);
        Assert.False(d.AllowAlways, "高危请求绝不能允许'本会话都允许'");
    }

    [Fact]
    public void Normal_request_keeps_allowAlways()
    {
        var d = PermissionChecker.Check(
            PermissionMode.Ask,
            PermissionChecker.Operation.WriteFile,
            CommandRisk.Safe,
            interactive: true);

        Assert.Equal(PermDecisionKind.NeedAsk, d.Kind);
        Assert.True(d.AllowAlways);
    }
}
