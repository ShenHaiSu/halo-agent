namespace HaloAgent.Desktop.Gpu;

/// <summary>GPU 三档决策。Win7/黑名单/虚拟机/崩溃史一律先上软件合成。</summary>
public enum GpuDecision
{
    /// <summary>启用 GPU 加速（默认，Win10/11 且驱动健康）。</summary>
    Enabled,
    /// <summary>禁用 GPU 合成但保留软件光栅（Win7 与黑名单首选）。</summary>
    SoftwareCompositing,
    /// <summary>完全禁用 GPU（最后手段）。</summary>
    FullyDisabled,
}

/// <summary>显卡驱动信息（注册表探测，不依赖 WMI）。</summary>
public sealed record GpuDriverInfo(
    string DriverName,
    string DeviceName,
    string DriverVersion,
    bool IsVirtualMachine);
