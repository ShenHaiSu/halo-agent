using CefSharp;
using CefSharp.WinForms;

using Microsoft.Extensions.Logging;

namespace HaloAgent.Desktop.Gpu;

/// <summary>
/// GPU 五级判定：①显式配置 → ②崩溃史 → ③OS(Win7) → ④驱动黑名单 → ⑤虚拟机。
/// 顺序不可调换。唯一决策入口。
/// </summary>
public static class GpuPolicy
{
    /// <summary>
    /// GPU 合成已知有问题的驱动标识（小写子串匹配）。
    /// 来源：Chromium gpu_driver_bug_list.json 中老驱动+Win7/8 条目 + 实际踩坑。
    /// </summary>
    private static readonly string[] BlacklistedDriverMarkers =
    {
        "ig4icd32",    // Intel GMA 950/3100/3150（Win7 时代核显）
        "ig4icd64",    // Intel GMA 64 位驱动
        "igd10um",     // Intel HD 2000/3000 的部分 Win7 驱动
        "nvwgf2um",    // NVIDIA Win7 老驱动（D3D10 路径）
        "nv4_disp",    // NVIDIA 更老的显示驱动
        "aticfx32",    // AMD Win7 老驱动
        "aticfx64",    // AMD Win7 老驱动 64 位
        "vmware svga", // VMware 老虚拟显卡
        "virtualbox",  // VirtualBox 增强驱动
        "vbox",        // VirtualBox 简写
        "qxl",         // QEMU/SPICE
        "cirrus",      // 极老虚拟显卡
    };

    public static GpuDecision Decide(
        bool isWin7,
        bool forceFlagExists,
        bool forceSoftwareFlagExists,
        GpuDriverInfo? driverInfo,
        bool lastRunCrashed,
        ILogger? log = null)
    {
        // ① 显式配置最高优先级。
        if (forceSoftwareFlagExists)
        {
            log?.LogInformation("GPU: FullyDisabled（force-software）");
            return GpuDecision.FullyDisabled;
        }
        if (forceFlagExists)
        {
            log?.LogInformation("GPU: SoftwareCompositing（disable-gpu）");
            return GpuDecision.SoftwareCompositing;
        }

        // ② 崩溃史：机器自己说话。
        if (lastRunCrashed)
        {
            log?.LogInformation("GPU: SoftwareCompositing（上次未正常退出，自动降级）");
            return GpuDecision.SoftwareCompositing;
        }

        // ③ Win7 一律软件合成。
        if (isWin7)
        {
            log?.LogInformation("GPU: SoftwareCompositing（Windows 7 保守默认）");
            return GpuDecision.SoftwareCompositing;
        }

        // ④ 驱动黑名单。
        if (driverInfo is not null)
        {
            var probe = (driverInfo.DriverName + " " + driverInfo.DeviceName).ToLowerInvariant();
            foreach (var marker in BlacklistedDriverMarkers)
            {
                if (probe.Contains(marker, StringComparison.Ordinal))
                {
                    log?.LogInformation("GPU: SoftwareCompositing（驱动命中黑名单 {Marker}）", marker);
                    return GpuDecision.SoftwareCompositing;
                }
            }
        }

        // ⑤ 虚拟机。
        if (driverInfo?.IsVirtualMachine == true)
        {
            log?.LogInformation("GPU: SoftwareCompositing（虚拟化环境）");
            return GpuDecision.SoftwareCompositing;
        }

        log?.LogInformation("GPU: Enabled（未命中任何降级条件）");
        return GpuDecision.Enabled;
    }

    /// <summary>决策对应的命令行 flags（不含 DisableGpuAcceleration 本体；可单测）。</summary>
    public static IReadOnlyList<string> FlagsFor(GpuDecision decision) => decision switch
    {
        GpuDecision.Enabled => Array.Empty<string>(),
        GpuDecision.SoftwareCompositing => new[]
        {
            "disable-gpu-compositing",
            "disable-gpu-vsync",
            "disable-accelerated-2d-canvas",
            "disable-accelerated-video-decode",
        },
        // FullyDisabled = 上述 4 个 + DisableGpuAcceleration()（等价 --disable-gpu）。
        // disable-software-rasterizer 默认不加（会全白）。
        GpuDecision.FullyDisabled => new[]
        {
            "disable-gpu-compositing",
            "disable-gpu-vsync",
            "disable-accelerated-2d-canvas",
            "disable-accelerated-video-decode",
        },
        _ => Array.Empty<string>(),
    };

    /// <summary>把决策翻译成 CefSettings 改动。disable-software-rasterizer 默认不加。</summary>
    public static void Apply(GpuDecision decision, CefSettings settings)
    {
        if (decision == GpuDecision.Enabled) return;
        if (decision == GpuDecision.FullyDisabled)
            settings.DisableGpuAcceleration(); // 等价于 --disable-gpu
        foreach (var key in FlagsFor(decision))
            if (!settings.CefCommandLineArgs.ContainsKey(key))
                settings.CefCommandLineArgs.Add(key, null);
        // ★ disable-software-rasterizer 会让页面完全不绘制（全白），默认不加。
        // 实测表见 docs/P3-宿主与CefSharp/05-验收目标.md §5.3。
        // AddIfAbsent(settings, "disable-software-rasterizer"); // ← 默认注释掉
    }
}
