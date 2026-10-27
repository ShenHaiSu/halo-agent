using Microsoft.Win32;

namespace HaloAgent.Desktop.Gpu;

/// <summary>从注册表读显卡驱动信息（毫秒级，不依赖 WMI；精简版缺键时返回 null）。</summary>
public static class GpuDriverProbe
{
    public static GpuDriverInfo? Probe()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null) return null;

            foreach (var subName in baseKey.GetSubKeyNames())
            {
                if (!subName.StartsWith("0", StringComparison.Ordinal)) continue;
                using var sub = baseKey.OpenSubKey(subName);
                if (sub is null) continue;
                var driverDesc = sub.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(driverDesc)) continue;
                var providerName = sub.GetValue("ProviderName") as string ?? "";
                var driverVersion = sub.GetValue("DriverVersion") as string ?? "";
                var deviceId = sub.GetValue("MatchingDeviceId") as string ?? "";
                var combined = (driverDesc + " " + providerName + " " + deviceId).ToLowerInvariant();
                var isVm = combined.Contains("vmware", StringComparison.Ordinal)
                        || combined.Contains("virtualbox", StringComparison.Ordinal)
                        || combined.Contains("hyper-v", StringComparison.Ordinal)
                        || combined.Contains("qxl", StringComparison.Ordinal)
                        || combined.Contains("cirrus", StringComparison.Ordinal)
                        || combined.Contains("parallels", StringComparison.Ordinal);
                return new GpuDriverInfo(providerName, driverDesc, driverVersion, isVm);
            }
        }
        catch
        {
            // 探测失败不致命，返回 null 让上层走保守默认。
        }
        return null;
    }
}
