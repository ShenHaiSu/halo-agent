namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// 按命令名分派判定规则。
///
/// ============ 设计 ============
/// 每个命令只看它**真正会导致破坏的参数**：
///   - rm 必须带 -r（不带 -r 只删文件，不算高危）
///   - chmod 必须带 -R
///   - find 必须带 -delete 或 -exec rm
/// 这样"正常危险操作的温和版本"（rm 单文件、chmod 单文件）被放行。
/// </summary>
public static class CommandRules
{
    /// <summary>前缀命令：`sudo rm -rf /` 要看的是 `rm`</summary>
    private static readonly HashSet<string> PrefixCommands = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "command", "builtin", "exec", "nohup", "setsid",
        "time", "nice", "ionice", "stdbuf", "env", "timeout",
    };

    /// <summary>会删东西的命令名（xargs / find -exec 用）</summary>
    private static readonly HashSet<string> DestructiveNames = new(StringComparer.Ordinal)
    {
        "rm", "shred", "unlink", "rmdir",
        "del", "erase", "rd",   // Windows
    };

    /// <summary>shell 解释器</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "ksh", "fish", "ash",
        "cmd", "powershell", "pwsh",
    };

    /// <summary>块设备路径前缀</summary>
    private static readonly string[] DevicePrefixes =
    {
        "/dev/sd", "/dev/hd", "/dev/vd", "/dev/xvd", "/dev/nvme",
        "/dev/mmcblk", "/dev/loop", "/dev/mapper/", "/dev/dm-",
        "/dev/disk", "/dev/rdisk", "/dev/fd0", "/dev/ram",
        @"\\.\physicaldrive",
    };

    /// <summary>判定一段简单命令</summary>
    public static CommandRisk JudgeSegment(List<string> argv, InspectorOptions opts, int depth = 0)
    {
        if (argv.Count == 0) return CommandRisk.Safe;

        // ---------- ① 重定向目标（echo x > /dev/sda） ----------
        for (int i = 0; i < argv.Count; i++)
        {
            if (IsRedirectOp(argv[i]) && i + 1 < argv.Count)
            {
                var tgt = argv[i + 1];
                var cls = ClassifyTarget(tgt, opts);
                if (cls is TargetClass.Dangerous d)
                    return CommandRisk.Blocked($"重定向写入{d.Reason}", d.Shown);
                if (cls is TargetClass.Uncertain u)
                    return CommandRisk.Uncertain("重定向目标无法确定", u.Detail);
            }
        }

        // ---------- ② 剥前缀 ----------
        var stripped = StripPrefixes(argv);
        if (stripped.Count == 0) return CommandRisk.Safe;

        var cmd = Basename(stripped[0]);
        var args = stripped.Skip(1).ToList();

        // ---------- ③ 解释器 -c 递归 ----------
        if (ShellNames.Contains(cmd))
        {
            // Windows 的 powershell 交给 WindowsRules
            if (cmd is "powershell" or "pwsh")
            {
                var wr = WindowsRules.Judge(cmd, stripped, opts.Cwd, opts.Home);
                if (wr is not null) return wr;
            }

            var script = ScriptArg(args, cmd);
            if (script is not null)
            {
                if (depth >= opts.MaxDepth)
                    return CommandRisk.Uncertain("嵌套过深的脚本调用", "判定达到递归上限");
                return DangerousCommandInspector.InspectInner(script, opts, depth + 1);
            }
            return CommandRisk.Safe;
        }

        // ---------- ④ Windows 特化 ----------
        {
            var wr = WindowsRules.Judge(cmd, stripped, opts.Cwd, opts.Home);
            if (wr is not null) return wr;
        }

        // ---------- ⑤ xargs 删除 ----------
        if (cmd == "xargs")
        {
            if (args.Any(a => DestructiveNames.Contains(Basename(a))))
                return CommandRisk.Uncertain(
                    "无法确定 xargs 的删除目标",
                    "待删路径来自标准输入，判定时看不到");
            return CommandRisk.Safe;
        }

        // ---------- ⑥ 按命令分派 ----------
        return cmd switch
        {
            "rm" => JudgeRm(args, opts),
            "find" => JudgeFind(args, opts),
            "dd" => JudgeDd(args, opts),
            var c when c.StartsWith("mkfs", StringComparison.Ordinal)
                => WorstOfTargets(Operands(args), opts, "格式化"),
            "fdisk" or "sfdisk" or "parted" or "sgdisk" or "wipefs" => JudgePartition(cmd, args, opts),
            "chmod" or "chown" or "chgrp" or "setfacl" => JudgePermissionChange(args, opts),
            "truncate" or "shred" => WorstOfTargets(Operands(args), opts, "清空/粉碎"),
            "git" => JudgeGit(args, opts),
            "shutdown" or "reboot" or "poweroff" or "halt" or "init" => CommandRisk.Uncertain(
                "系统级操作，请确认", $"{cmd} 会重启/关机，影响本机所有工作"),
            _ => CommandRisk.Safe,
        };
    }

    // ============================================================
    //  各命令的具体判定
    // ============================================================

    private static CommandRisk JudgeRm(List<string> args, InspectorOptions opts)
    {
        bool recursive = HasShortFlag(args, 'r') || HasShortFlag(args, 'R') || HasLongFlag(args, "recursive");
        if (!recursive) return CommandRisk.Safe;   // rm file.txt 不算高危
        return WorstOfTargets(Operands(args), opts, "递归删除");
    }

    private static CommandRisk JudgeFind(List<string> args, InspectorOptions opts)
    {
        bool delete = args.Any(a => a == "-delete");
        bool execRm = args.Zip(args.Skip(1))
            .Any(p => (p.First == "-exec" || p.First == "-execdir")
                      && DestructiveNames.Contains(Basename(p.Second)));

        if (!delete && !execRm) return CommandRisk.Safe;

        // find 的第一个非选项词是起始目录
        var start = args.FirstOrDefault(t => !t.StartsWith('-'));
        if (start is null) return CommandRisk.Safe;
        return WorstOfTargets(new List<string> { start }, opts, "删除");
    }

    private static CommandRisk JudgeDd(List<string> args, InspectorOptions opts)
    {
        foreach (var a in args)
        {
            if (!a.StartsWith("of=", StringComparison.Ordinal)) continue;
            var target = a[3..];
            var cls = ClassifyTarget(target, opts);
            return cls switch
            {
                TargetClass.Dangerous d => CommandRisk.Blocked($"dd 写入{d.Reason}", d.Shown),
                TargetClass.Uncertain u => CommandRisk.Uncertain("无法确定 dd 的写入目标", u.Detail),
                _ => CommandRisk.Safe,
            };
        }
        return CommandRisk.Safe;
    }

    private static CommandRisk JudgePartition(
        string cmd, List<string> args, InspectorOptions opts)
    {
        // -l / --list 是只读列举
        if (HasShortFlag(args, 'l') || HasLongFlag(args, "list")) return CommandRisk.Safe;
        return WorstOfTargets(Operands(args), opts, "分区/擦除");
    }

    private static CommandRisk JudgePermissionChange(List<string> args, InspectorOptions opts)
    {
        if (!HasShortFlag(args, 'R') && !HasLongFlag(args, "recursive"))
            return CommandRisk.Safe;
        return WorstOfTargets(Operands(args), opts, "递归改权限");
    }

    private static CommandRisk JudgeGit(List<string> args, InspectorOptions opts)
    {
        _ = opts;
        if (!args.Any(a => a == "push")) return CommandRisk.Safe;

        // --force-with-lease 是安全的强推
        bool lease = args.Any(a =>
            a == "--force-with-lease" || a.StartsWith("--force-with-lease=", StringComparison.Ordinal));

        if (!lease && (HasLongFlag(args, "force") || HasShortFlag(args, 'f')))
            return CommandRisk.Blocked("git 强制推送(--force)，可能覆盖远端历史", "远端分支");

        return CommandRisk.Safe;
    }

    // ============================================================
    //  目标路径分类
    // ============================================================

    internal abstract record TargetClass
    {
        public sealed record Safe : TargetClass;
        /// <summary>（原因, 规范化后的目标）</summary>
        public sealed record Dangerous(string Reason, string Shown) : TargetClass;
        public sealed record Uncertain(string Detail) : TargetClass;
    }

    internal static TargetClass ClassifyTarget(string raw, InspectorOptions opts)
    {
        var res = PathNormalizer.Resolve(raw, opts.Cwd, opts.Home);
        switch (res)
        {
            case PathResolution.Unknown u:
                return new TargetClass.Uncertain(u.Reason);
            case PathResolution.Invalid i:
                return new TargetClass.Uncertain(i.Reason);
            case PathResolution.Ok ok:
                var why = DangerousWhy(ok.Path, opts);
                return why is null
                    ? new TargetClass.Safe()
                    : new TargetClass.Dangerous(why, ok.Path);
            default:
                return new TargetClass.Safe();
        }
    }

    /// <summary>高危目标：根 / 家目录 / 块设备 / 工作目录或其上级</summary>
    internal static string? DangerousWhy(string path, InspectorOptions opts)
    {
        // 根目录（POSIX / 与 Windows 根盘符）
        if (path == "/" || path == "\\") return "根目录";
        if (path.Length is 2 or 3 && path[1] == ':') return "根盘符";

        // 家目录
        if (!string.IsNullOrEmpty(opts.Home) && PathNormalizer.PathEquals(path, opts.Home))
            return "家目录";

         // 块设备（Windows 规范化后是反斜杠，先统一成 `/` 再比前缀）
         var lower = path.Replace('\\', '/').ToLowerInvariant();
         if (DevicePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
             return "块设备";

        // 工作目录或其上级
        if (PathNormalizer.IsSameOrAncestor(opts.Cwd, path))
            return "当前工作目录或其上级目录";

        return null;
    }

    private static CommandRisk WorstOfTargets(
        List<string> targets, InspectorOptions opts, string verb)
    {
        CommandRisk worst = CommandRisk.Safe;
        foreach (var t in targets)
        {
            switch (ClassifyTarget(t, opts))
            {
                case TargetClass.Dangerous d:
                    return CommandRisk.Blocked($"检测到{verb}{d.Reason}", d.Shown);
                case TargetClass.Uncertain u:
                    if (worst.Rank < 1)
                        worst = CommandRisk.Uncertain($"{verb}的目标无法确定", u.Detail);
                    break;
            }
        }
        return worst;
    }

    // ============================================================
    //  参数工具
    // ============================================================

    internal static List<string> StripPrefixes(List<string> argv)
    {
        int start = 0;

        // 剥前置变量赋值（FOO=1 rm ...）
        while (start < argv.Count && IsAssignment(argv[start])) start++;

        // 剥前缀命令
        while (start < argv.Count)
        {
            var name = Basename(argv[start]);
            if (!PrefixCommands.Contains(name)) break;
            start++;

            // 剥该前缀自己的选项（带值的要跳过值）
            while (start < argv.Count)
            {
                var t = argv[start];
                bool takesValue = PrefixFlagTakesValue(name, t);
                bool isFlagOrAssign = (t.StartsWith('-') && t.Length > 1) || t.Contains('=');
                bool isDuration = t.Length > 0 && t.All(c => IsAsciiDigit(c) || c is '.' or 's' or 'm' or 'h');

                if (!isFlagOrAssign && !isDuration) break;
                start++;
                if (takesValue && start < argv.Count) start++;
            }
        }

        return start >= argv.Count ? new List<string>() : argv.GetRange(start, argv.Count - start);
    }

    private static bool IsAssignment(string t)
    {
        var eq = t.IndexOf('=');
        if (eq <= 0) return false;
        var name = t[..eq];
        if (name.Length == 0) return false;
        var first = name[0];
        if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_'))
            return false;
        return name.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '_');
    }

    private static bool PrefixFlagTakesValue(string prefix, string flag) => prefix switch
    {
        "sudo" or "doas" => flag is "-u" or "-g" or "-h" or "-p" or "-C" or "-U" or "-r" or "-t",
        "nice" or "ionice" => flag is "-n" or "-c" or "-p" or "-u",
        "timeout" => flag is "-s" or "-k",
        "env" or "stdbuf" => flag is "-u" or "-C" or "-o" or "-e",
        _ => false,
    };

    internal static string Basename(string s)
    {
        var name = s;
        var idx = name.LastIndexOfAny(new[] { '/', '\\' });
        if (idx >= 0 && idx + 1 < name.Length) name = name[(idx + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    private static bool IsRedirectOp(string t)
    {
        if (t.Length == 0) return false;
        if (!(t.Contains('>') || t.Contains('<'))) return false;
        return t.All(c => c is '>' or '<' or '|' or '&' or (>= '0' and <= '9'));
    }

    private static bool HasShortFlag(List<string> args, char ch) =>
        args.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Contains(ch));

    private static bool HasLongFlag(List<string> args, string name) =>
        args.Any(a => a == "--" + name || a.StartsWith("--" + name + "=", StringComparison.Ordinal));

    internal static List<string> Operands(List<string> args)
    {
        var result = new List<string>();
        bool noMoreFlags = false;
        foreach (var a in args)
        {
            if (!noMoreFlags && a == "--") { noMoreFlags = true; continue; }
            if (!noMoreFlags && a.StartsWith('-') && a.Length > 1) continue;
            result.Add(a);
        }
        return result;
    }

    private static string? ScriptArg(List<string> args, string shell)
    {
        if (shell is "cmd")
        {
            // cmd /C "command"
            for (int i = 0; i < args.Count; i++)
                if (args[i].Equals("/c", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Count ? args[i + 1] : null;
            return null;
        }

        if (shell is "powershell" or "pwsh") return null;   // 交给 WindowsRules

        // POSIX: bash -c 'script'（-c 可能在合并 flag 里，如 -lc）
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Contains('c'))
                return i + 1 < args.Count ? args[i + 1] : null;
        }
        return null;
    }

    // .NET 6 没有 char.IsAsciiDigit（.NET 7+），手写判断。
    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
}

/// <summary>判定所需的上下文。</summary>
public sealed record InspectorOptions
{
    public string Cwd { get; init; } = "";
    public string? Home { get; init; }
    /// <summary>递归深度上限（防 `sh -c 'sh -c ...'` 套娃）</summary>
    public int MaxDepth { get; init; } = 3;
}
