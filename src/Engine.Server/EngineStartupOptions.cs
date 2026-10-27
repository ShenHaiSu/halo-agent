namespace HaloAgent.Engine.Server;

public enum EngineRunMode { Stdio, Pipe, InProc }

/// <summary>
/// 引擎的命令行参数。
/// 环境变量（优先级低于命令行）：
///   HALO_LOG        trace|debug|info|warn|error，默认 info
///   HALO_DATA_DIR   覆盖数据目录（测试/多实例用）
/// </summary>
public sealed record EngineStartupOptions
{
    public EngineRunMode Mode { get; init; } = EngineRunMode.Pipe;
    public string? PipeName { get; init; }
    public string DataDir { get; init; } = DefaultDataDir();
    public string LogLevel { get; init; } = Env("HALO_LOG") ?? "info";
    public bool ShowHelp { get; init; }
    public bool Diagnostics { get; init; }
    public int ExitCodeSuccess => 0;

    public static string DefaultDataDir()
    {
        var over = Env("HALO_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(over)) return over!;
        // 铁律：数据目录 = %USERPROFILE%\.haloagent
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Path.GetTempPath(), "haloagent");
        return Path.Combine(home, ".haloagent");
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    public static EngineStartupOptions Parse(string[] args)
    {
        var o = new EngineStartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdio":
                    o = o with { Mode = EngineRunMode.Stdio };
                    break;
                case "--pipe-name":
                    o = o with { Mode = EngineRunMode.Pipe, PipeName = Next(args, ref i) };
                    break;
                case "--data-dir":
                    o = o with { DataDir = Next(args, ref i)! };
                    break;
                case "--log":
                    o = o with { LogLevel = Next(args, ref i)! };
                    break;
                case "--diagnostics":
                    o = o with { Diagnostics = true };
                    break;
                case "-h":
                case "--help":
                    o = o with { ShowHelp = true };
                    break;
                default:
                    // 未知参数不静默吞：直接失败，避免"我传了却没生效"
                    throw new ArgumentException($"未知参数: {args[i]}（用 --help 查看可用参数）");
            }
        }
        return o;
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少值");
        return args[++i];
    }

    public const string HelpText =
        "HaloAgent 引擎进程\n" +
        "\n" +
        "用法: HaloAgent.Engine.exe [选项]\n" +
        "\n" +
        "  --stdio                走 stdin/stdout 收发帧（测试与独立联调）\n" +
        "  --pipe-name <名字>     走命名管道（默认模式；名字由宿主指定）\n" +
        "  --data-dir <路径>      数据目录（默认 %USERPROFILE%\\.haloagent）\n" +
        "  --log <级别>           trace|debug|info|warn|error（默认 info）\n" +
        "  --diagnostics          打印环境摘要后退出\n" +
        "  -h, --help             显示本帮助\n" +
        "\n" +
        "示例:\n" +
        "  echo {\"t\":\"req\",\"id\":1,\"m\":\"system.ping\"} | HaloAgent.Engine.exe --stdio\n";
}
