namespace HaloAgent.Desktop;

/// <summary>宿主启动参数（P0 只有很少几个）。record：支持 with 表达式。</summary>
public sealed record DesktopStartup
{
    /// <summary>开发态 URL；为空 = 走 app:// 生产加载</summary>
    public string? DevUrl { get; init; }

    public bool Diagnostics { get; init; }

    public static DesktopStartup Parse(string[] args)
    {
        var o = new DesktopStartup();
        var envDev = Environment.GetEnvironmentVariable("HALO_DEV_URL");
        if (!string.IsNullOrWhiteSpace(envDev)) o = o with { DevUrl = envDev.Trim() };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dev-url" when i + 1 < args.Length:
                    o = o with { DevUrl = args[++i] };
                    break;
                case "--diagnostics":
                    o = o with { Diagnostics = true };
                    break;
            }
        }
        return o;
    }
}
