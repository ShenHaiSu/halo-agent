namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 宿主命令行选项（P3 冻结 12 参数 + 位置参数）。
/// --dev/--dev-url/--diagnostics/--data-dir/--log-dir/--cwd/--disable-gpu/
/// --force-software/--engine-args/--no-engine/--reset-window + 位置路径。
/// </summary>
public sealed class HostOptions
{
    public bool IsDev { get; init; }
    public string? DevUrl { get; init; }
    public bool Diagnostics { get; init; }
    public string DataDir { get; init; } = "";
    public string LogDir { get; init; } = "";
    public string Cwd { get; init; } = "";
    public bool DisableGpu { get; init; }
    public bool ForceSoftware { get; init; }
    public string? EngineArgs { get; init; }
    public bool NoEngine { get; init; }
    public bool ResetWindow { get; init; }
    public IReadOnlyList<string> OpenPaths { get; init; } = Array.Empty<string>();

    public static HostOptions Parse(string[] args)
    {
        bool dev = false, diag = false, disableGpu = false, forceSw = false;
        bool noEngine = false, resetWindow = false;
        string? devUrl = null, dataDir = null, logDir = null, cwd = null, engineArgs = null;
        var openPaths = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var (key, val) = SplitOption(a);
                // 支持空格形式：--data-dir X（X 不以 -- 开头时视为值）。
                if (val is null && NeedsValue(key) && i + 1 < args.Length &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    val = args[++i];
                switch (key)
                {
                    case "--dev": dev = true; break;
                    case "--dev-url": devUrl = Require(val, key); break;
                    case "--diagnostics": diag = true; break;
                    case "--data-dir": dataDir = Require(val, key); break;
                    case "--log-dir": logDir = Require(val, key); break;
                    case "--cwd": cwd = Require(val, key); break;
                    case "--disable-gpu": disableGpu = true; break;
                    case "--force-software": forceSw = true; break;
                    case "--engine-args": engineArgs = Require(val, key); break;
                    case "--no-engine": noEngine = true; break;
                    case "--reset-window": resetWindow = true; break;
                    default:
                        throw new ArgumentException($"未知参数：{key}");
                }
            }
            else
            {
                openPaths.Add(a);
            }
        }

        // 环境变量兜底（参数优先）。
        dev |= Environment.GetEnvironmentVariable("HALO_DEV") == "1";
        var envDevUrl = Environment.GetEnvironmentVariable("HALO_DEV_URL");
        if (devUrl is null && !string.IsNullOrWhiteSpace(envDevUrl)) devUrl = envDevUrl.Trim();
        dataDir ??= Environment.GetEnvironmentVariable("HALO_DATA_DIR");

        dataDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".haloagent");
        if (string.IsNullOrWhiteSpace(dataDir)) dataDir = Path.Combine(Path.GetTempPath(), "haloagent");
        dataDir = Path.GetFullPath(dataDir);

        logDir ??= Path.Combine(dataDir, "logs");
        logDir = Path.GetFullPath(logDir);

        cwd = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);

        if (!string.IsNullOrWhiteSpace(devUrl) &&
            !devUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !devUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--dev-url 必须是 http/https 地址");

        // 发布版禁止 --dev（防用户脚本打开 DevTools / 指向任意网页）。
        if (dev && !BuildInfo.AllowsDevMode)
            throw new ArgumentException("此版本不支持 --dev 模式（仅开发构建可用）");

        return new HostOptions
        {
            IsDev = dev,
            DevUrl = devUrl,
            Diagnostics = diag,
            DataDir = dataDir,
            LogDir = logDir,
            Cwd = cwd,
            DisableGpu = disableGpu,
            ForceSoftware = forceSw,
            EngineArgs = engineArgs,
            NoEngine = noEngine,
            ResetWindow = resetWindow,
            OpenPaths = openPaths,
        };
    }

    /// <summary>命令行/flag 文件合并为最终 GPU 强制档位（命令行 &gt; flag 文件 &gt; 自动）。</summary>
    public static (bool disableGpu, bool forceSoftware) MergeGpuFlags(HostOptions opts)
    {
        var baseDir = AppContext.BaseDirectory;
        var flagDisableGpu = File.Exists(Path.Combine(baseDir, "disable-gpu.flag"));
        var flagForceSw = File.Exists(Path.Combine(baseDir, "force-software.flag"));
        return (opts.DisableGpu || flagDisableGpu, opts.ForceSoftware || flagForceSw);
    }

    public string EffectiveDevUrl => DevUrl ?? "http://localhost:5173";

    private static (string Key, string? Value) SplitOption(string arg)
    {
        var i = arg.IndexOf('=');
        if (i >= 0) return (arg[..i], arg[(i + 1)..]);
        return (arg, null);
    }

    private static bool NeedsValue(string key) => key is
        "--dev-url" or "--data-dir" or "--log-dir" or "--cwd" or "--engine-args";

    private static string Require(string? v, string key) =>
        string.IsNullOrEmpty(v) ? throw new ArgumentException($"{key} 需要一个值") : v;
}
