using HaloAgent.Desktop.Gpu;
using Xunit;

namespace HaloAgent.Host.Tests.Gpu;

/// <summary>
/// GpuPolicy 五级判定矩阵（纯函数，可脱离 CEF 运行；
/// Apply 只 new CefSettings，不需要 Cef.Initialize）。
/// </summary>
public sealed class GpuPolicyTests
{
    private static readonly GpuDriverInfo Healthy = new(
        DriverName: "NVIDIA",
        DeviceName: "NVIDIA GeForce RTX 3060",
        DriverVersion: "31.0.15.3623",
        IsVirtualMachine: false);

    // 注意：黑名单匹配的是驱动文件关键字（如 ig4icd32），不是设备显示名。
    // 真实探测里该关键字来自 ProviderName/DeviceId 组合串。
    private static readonly GpuDriverInfo Blacklisted = new(
        DriverName: "Intel Corporation ig4icd32",
        DeviceName: "Intel(R) G41 Express Chipset",
        DriverVersion: "8.15.10.2302",
        IsVirtualMachine: false);

    private static readonly GpuDriverInfo Virtual = new(
        DriverName: "VMware, Inc.",
        DeviceName: "VMware SVGA II Adapter",
        DriverVersion: "13.4.0.0",
        IsVirtualMachine: true);

    [Theory]
    // isWin7, flag, forceSw, blacklisted, crashed, expect
    [InlineData(false, false, false, false, false, GpuDecision.Enabled)] // 1
    [InlineData(false, false, false, false, true, GpuDecision.SoftwareCompositing)] // 2 崩溃降级
    [InlineData(false, false, false, true, false, GpuDecision.SoftwareCompositing)] // 3 黑名单
    [InlineData(true, false, false, false, false, GpuDecision.SoftwareCompositing)] // 5 Win7
    [InlineData(true, true, false, false, false, GpuDecision.SoftwareCompositing)] // 6
    [InlineData(true, false, true, false, false, GpuDecision.FullyDisabled)] // 7
    [InlineData(false, true, false, false, false, GpuDecision.SoftwareCompositing)] // 8
    // 4 虚拟机由 Virtual_machine_degrades 单独覆盖
    [InlineData(false, false, true, false, false, GpuDecision.FullyDisabled)] // 9
    [InlineData(false, true, true, false, false, GpuDecision.FullyDisabled)] // 10 forceSw 优先
    [InlineData(true, true, true, false, true, GpuDecision.FullyDisabled)] // 11 配置最高
    public void Matrix(bool isWin7, bool flag, bool forceSw, bool blacklisted,
                       bool crashed, GpuDecision expect)
    {
        var driver = blacklisted ? Blacklisted : Healthy;
        var actual = GpuPolicy.Decide(isWin7, flag, forceSw, driver, crashed);
        Assert.Equal(expect, actual);
    }

    [Fact]
    public void Virtual_machine_degrades()
    {
        var actual = GpuPolicy.Decide(false, false, false, Virtual, false);
        Assert.Equal(GpuDecision.SoftwareCompositing, actual);
    }

    [Fact]
    public void Null_driver_does_not_degrade()
    {
        var actual = GpuPolicy.Decide(false, false, false, null, false);
        Assert.Equal(GpuDecision.Enabled, actual);
    }
    [Fact]
    public void FullyDisabled_does_not_add_rasterizer_flag()
    {
        var flags = GpuPolicy.FlagsFor(GpuDecision.FullyDisabled);
        Assert.DoesNotContain("disable-software-rasterizer", flags);
        Assert.Equal(4, flags.Count);
    }

    [Fact]
    public void Software_compositing_flags_are_exactly_four()
    {
        var flags = GpuPolicy.FlagsFor(GpuDecision.SoftwareCompositing);
        Assert.Equal(new[]
        {
            "disable-gpu-compositing",
            "disable-gpu-vsync",
            "disable-accelerated-2d-canvas",
            "disable-accelerated-video-decode",
        }, flags);
    }

    [Fact]
    public void Enabled_adds_nothing()
    {
        Assert.Empty(GpuPolicy.FlagsFor(GpuDecision.Enabled));
    }
}
