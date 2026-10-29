namespace HaloAgent.Engine.Cli;

public sealed record CliOptions
{
    public string? Prompt { get; init; }
    public string Permission { get; init; } = "ask";
    public int? MaxTurns { get; init; }
    public int? Retry { get; init; }
    public string? Cwd { get; init; }
    public string? DataDir { get; init; }
    public bool Json { get; init; }
    public bool ShowHelp { get; init; }
    public bool Stdio { get; init; }
    public bool Diagnostics { get; init; }
    public string? PipeName { get; init; }
    public string LogLevel { get; init; } = "info";

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--prompt": o = o with { Prompt = Next(args, ref i) }; break;
                case "--permission": o = o with { Permission = Next(args, ref i) ?? "ask" }; break;
                case "--max-turns": o = o with { MaxTurns = int.Parse(Next(args, ref i)!) }; break;
                case "--retry": o = o with { Retry = int.Parse(Next(args, ref i)!) }; break;
                case "--cwd": o = o with { Cwd = Next(args, ref i) }; break;
                case "--data-dir": o = o with { DataDir = Next(args, ref i) }; break;
                case "--json": o = o with { Json = true }; break;
                case "--stdio": o = o with { Stdio = true }; break;
                case "--diagnostics": o = o with { Diagnostics = true }; break;
                case "--pipe-name": o = o with { PipeName = Next(args, ref i) }; break;
                case "--log": o = o with { LogLevel = Next(args, ref i) ?? "info" }; break;
                // P10：--provider/--model/--base-url/--api-key/--proxy/--no-proxy 已删除（仅保留 --data-dir）
                case "--provider":
                case "--model":
                case "--base-url":
                case "--api-key":
                case "--proxy":
                case "--no-proxy":
                    throw new ArgumentException("该参数已删除（P10 破坏性）：请改用 config.json 的 providers/active 配置（仅保留 --data-dir）: " + args[i]);
                case "-h":
                case "--help": o = o with { ShowHelp = true }; break;
                default:
                    throw new ArgumentException("未知参数: " + args[i] + "（用 --help 查看）");
            }
        }
        return o;
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " 缺少值");
        return args[++i];
    }

    public const string HelpText =
        "HaloAgent 无头模式\n" +
        "\n" +
        "用法: HaloAgent.Cli -p \"<任务>\" [选项]\n" +
        "\n" +
        "  -p, --prompt <text>     任务文本（必需）\n" +
        "  --permission <mode>     ask | acceptEdits | bypassPermissions | yolo（默认 ask）\n" +
        "  --max-turns <n>         轮次上限（0 = 不限）\n" +
        "  --retry <n>             弱网重试次数（0..8）\n" +
        "  --cwd <dir>             工作目录\n" +
        "  --data-dir <dir>        数据目录\n" +
        "  --json                  输出 JSON 结果\n" +
        "  --stdio                 走 stdin/stdout 收发帧（与引擎进程同行为）\n" +
        "  --pipe-name <名字>      走命名管道收发帧\n" +
        "  --log <级别>            trace|debug|info|warn|error（默认 info）\n" +
        "  --diagnostics           打印环境摘要后退出\n" +
        "  -h, --help              显示本帮助\n" +
        "\n" +
        "退出码:\n" +
        "  0 成功  1 任务失败  2 参数错误  3 未配置  4 被中断\n";
}
