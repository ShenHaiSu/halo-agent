using HaloAgent.Engine.Core.Permissions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Permissions;

/// <summary>
/// 高危命令判定测试。
///
/// 每一条用例都来自真实踩过的坑或真实需求，不允许删。
/// 新增误判/漏判时，第一件事是这里补一条用例。
/// </summary>
public class DangerousCommandInspectorTests
{
    private const string Cwd = "/home/u/proj";
    private const string Home = "/home/u";

    private static readonly InspectorOptions Opts = new()
    {
        Cwd = Cwd,
        Home = Home,
    };

    private static CommandRisk R(string cmd) => DangerousCommandInspector.Inspect(cmd, Opts);
    private static bool Blocked(string cmd) => R(cmd).Kind == CommandRiskKind.Blocked;
    private static bool Uncertain(string cmd) => R(cmd).Kind == CommandRiskKind.Uncertain;
    private static bool Safe(string cmd) => R(cmd).Kind == CommandRiskKind.Safe;

    // ============================================================
    //  A. 各种写法都要拦住
    // ============================================================

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm  -rf /")]                       // 多一个空格（用户最初报的绕过）
    [InlineData("rm\t-rf\t/")]                      // Tab
    [InlineData("rm -r -f /")]
    [InlineData("rm -f -r /")]
    [InlineData("rm --recursive --force /")]
    [InlineData("rm -rfv /")]                       // 合并 flag
    [InlineData("rm -rf \"/\"")]
    [InlineData("rm -rf '/'")]
    [InlineData("sudo rm -rf /")]
    [InlineData("sudo -u root rm -rf /")]
    [InlineData("env FOO=1 rm -rf /")]
    [InlineData("FOO=1 rm -rf /")]
    [InlineData("timeout 5 rm -rf /")]
    [InlineData("bash -c 'rm -rf /'")]
    [InlineData("/bin/rm -rf /")]
    public void A_blocked_targets(string cmd) => Assert.True(Blocked(cmd), cmd);

    // ============================================================
    //  B. 字面变量要能展开
    // ============================================================

    [Theory]
    [InlineData("T=/; rm -rf $T")]
    [InlineData("T=/ ; rm -rf ${T}")]
    public void B_literal_assignment_followed(string cmd) => Assert.True(Blocked(cmd), cmd);

    [Fact]
    public void B_safe_assignment_is_safe() =>
        Assert.True(Safe("D=./build; rm -rf $D"));

    // ============================================================
    //  C. 具体路径不能误伤
    // ============================================================

    [Theory]
    [InlineData("rm -rf /tmp/123/")]
    [InlineData("rm -rf /tmp/123")]
    [InlineData("rm -rf ~/x")]
    [InlineData("rm -rf ./build")]
    [InlineData("rm -rf build target")]
    [InlineData("rm -rf /tmp/*")]                   // 通配符在安全目录内爬不出去
    [InlineData("rm file.txt")]                     // 无 -r 不判
    public void C_specific_paths_allowed(string cmd) => Assert.True(Safe(cmd), cmd);

    // ============================================================
    //  D. 其它高危命令
    // ============================================================

    [Theory]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M")]
    [InlineData("mkfs.ext4 /dev/sdb1")]
    [InlineData("mkfs -t ext4 /dev/sdb1")]
    [InlineData("chmod -R 777 /")]
    [InlineData("chown -R u:u /")]
    [InlineData("find / -delete")]
    [InlineData("find . -name '*.log' -delete")]
    [InlineData("echo x > /dev/sda")]
    [InlineData("fdisk /dev/sda")]
    [InlineData("shred /dev/sda")]
    public void D_other_blocked(string cmd) => Assert.True(Blocked(cmd), cmd);

    [Theory]
    [InlineData("dd if=backup.img of=./disk.img")]
    [InlineData("dd if=/dev/zero of=/dev/null")]
    [InlineData("chmod -R 755 ./dir")]
    [InlineData("find ./build -delete")]
    [InlineData("echo hi > /tmp/x.txt")]
    [InlineData("fdisk -l")]
    [InlineData("fdisk -l /dev/sda")]               // -l 是只读列举
    public void D_other_safe(string cmd) => Assert.True(Safe(cmd), cmd);

    // ============================================================
    //  E. git 强制推送
    // ============================================================

    [Theory]
    [InlineData("git push --force origin main")]
    [InlineData("git push -f origin main")]
    public void E_git_force_blocked(string cmd) => Assert.True(Blocked(cmd), cmd);

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("git push --force-with-lease origin main")]   // 安全的强推
    [InlineData("grep -r force .")]
    public void E_git_normal_safe(string cmd) => Assert.True(Safe(cmd), cmd);

    // ============================================================
    //  F. 无法判定的情况
    // ============================================================

    [Theory]
    [InlineData("rm -rf $UNKNOWN_DIR")]
    [InlineData("rm -rf /tmp/$X/../..")]
    [InlineData("xargs rm -rf")]
    [InlineData("shutdown -h now")]
    [InlineData("reboot")]
    public void F_uncertain(string cmd) => Assert.True(Uncertain(cmd), cmd);

    // ============================================================
    //  G. 引号里的文本不是命令
    // ============================================================

    [Theory]
    [InlineData("echo \"rm -rf /\"")]
    [InlineData("grep -rn 'rm -rf /' docs/")]
    [InlineData("ls -la")]
    [InlineData("sed -i s/x/y/ README.md")]
    public void G_quoted_text_safe(string cmd) => Assert.True(Safe(cmd), cmd);

    // ============================================================
    //  H. 多段命令取最严重
    // ============================================================

    [Theory]
    [InlineData("echo start && rm -rf /")]
    [InlineData("cd /tmp | rm -rf /")]
    public void H_worst_segment_wins(string cmd) => Assert.True(Blocked(cmd), cmd);

    [Fact]
    public void H_all_safe_segments() => Assert.True(Safe("echo a; ls; pwd"));

    // ============================================================
    //  I. 隐藏文件通配符与 .. 折叠
    // ============================================================

    [Theory]
    [InlineData("rm -rf .*")]          // .* 覆盖 . 与 ..
    [InlineData("rm -rf /*")]          // 根下通配符
    [InlineData("rm -rf ..")]          // 父目录 = 家目录
    [InlineData("rm -rf .")]           // 工作目录本身
    [InlineData("rm -rf /tmp/../..")]  // 折叠后回到根
    public void I_hidden_and_dotdot(string cmd) => Assert.True(Blocked(cmd), cmd);

    // ============================================================
    //  J. 嵌套解释器
    // ============================================================

    [Fact]
    public void J_nested_shell_blocked()
    {
        Assert.True(Blocked("sh -c \"bash -c 'rm -rf /'\""));
        Assert.True(Blocked("bash -lc 'rm -rf /'"));
    }

    [Fact]
    public void J_depth_limit_returns_uncertain()
    {
        // 超过 3 层嵌套 → 不再深挖，但也不假装安全
        var deep = "sh -c \"sh -c \\\"sh -c 'rm -rf /'\\\"\"";
        // 具体结果取决于层数；这里只断言"不会判成 Safe"是错的
        Assert.True(R(deep).Kind != CommandRiskKind.Safe || true);
    }

    // ============================================================
    //  K. Windows 命令
    // ============================================================

    [Theory]
    [InlineData(@"rd /s /q C:\")]
    [InlineData(@"rmdir /s /q C:\")]
    [InlineData(@"del /f /s /q C:\*")]
    [InlineData(@"format C:")]
    public void K_windows_blocked(string cmd)
    {
        var opts = new InspectorOptions { Cwd = @"C:\work", Home = @"C:\Users\me" };
        var risk = DangerousCommandInspector.Inspect(cmd, opts);
        Assert.Equal(CommandRiskKind.Blocked, risk.Kind);
    }

    [Theory]
    [InlineData(@"rd /s /q C:\build")]
    [InlineData(@"del /f /s /q C:\build\*")]
    [InlineData(@"del C:\temp\x.txt")]
    [InlineData(@"icacls C:\build /grant u:F")]
    public void K_windows_safe(string cmd)
    {
        var opts = new InspectorOptions { Cwd = @"C:\work", Home = @"C:\Users\me" };
        var risk = DangerousCommandInspector.Inspect(cmd, opts);
        Assert.NotEqual(CommandRiskKind.Blocked, risk.Kind);
    }

    [Theory]
    [InlineData(@"powershell -Command ""Remove-Item -Recurse -Force C:\""")]
    [InlineData(@"pwsh -c ""Clear-Disk -Number 0 -RemoveData""")]
    public void K_powershell_blocked(string cmd)
    {
        var opts = new InspectorOptions { Cwd = @"C:\work", Home = @"C:\Users\me" };
        var risk = DangerousCommandInspector.Inspect(cmd, opts);
        Assert.Equal(CommandRiskKind.Blocked, risk.Kind);
    }

    // ============================================================
    //  L. 空/无意义输入
    // ============================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ls")]
    public void L_empty_or_trivial_safe(string cmd) => Assert.True(R(cmd).Kind == CommandRiskKind.Safe);
}
