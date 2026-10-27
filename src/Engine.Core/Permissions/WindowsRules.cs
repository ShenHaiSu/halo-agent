namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// Windows 平台的危险命令规则。
///
/// ============ 为什么必须单独一份 ============
/// 参考 POSIX 实现只懂 rm/mkfs/dd，而 Windows 用户和模型会用：
///   - cmd 内建：rd/rmdir、del/erase、format、diskpart、takeown、icacls
///   - PowerShell：Remove-Item、Clear-Disk、Format-Volume、Set-ExecutionPolicy
///   - 注册表：reg delete、bcdedit
/// 这些都不在 POSIX 规则里，不补就是裸奔。
///
/// ============ 判定策略 ============
/// 与 POSIX 一致：解析目标路径 → 规范化 → 与"根盘符 / 家目录 / 工作目录"比较。
/// 不按字符串匹配，所以 `rd /s /q C:\build` 放行、`rd /s /q C:\` 拦截。
/// </summary>
public static class WindowsRules
{
    /// <summary>会删除东西的 cmd 命令</summary>
    private static readonly string[] DeleteCommands = { "del", "erase", "rd", "rmdir" };

    /// <summary>格式化/擦除类命令</summary>
    private static readonly string[] FormatCommands = { "format", "diskpart", "cipher" };

    /// <summary>权限/属主变更命令</summary>
    private static readonly string[] AclCommands = { "icacls", "cacls", "takeown", "attrib" };

    /// <summary>注册表/引导类命令（不确定要确认）</summary>
    private static readonly string[] SystemCommands = { "reg", "bcdedit", "bootrec", "sfc" };

    /// <summary>PowerShell 危险 cmdlet（小写比较）</summary>
    private static readonly string[] DangerCmdlets =
    {
        "remove-item", "ri", "rm", "del", "erase", "rd", "rmdir",
        "clear-disk", "clear-content", "clc",
        "format-volume", "initialize-disk",
        "set-executionpolicy", "disable-windowsoptionalfeature",
        "stop-computer", "restart-computer",
    };

    /// <summary>
    /// 判定一条 Windows 命令。返回 null 表示"不是我能判的"（交给 POSIX 规则或 Safe）。
    /// </summary>
    public static CommandRisk? Judge(string cmd, List<string> argv, string cwd, string? home)
    {
        // ---------- PowerShell（powershell / pwsh 加 -Command） ----------
        if (cmd is "powershell" or "pwsh")
        {
            var script = ExtractPowerShellScript(argv);
            if (script is null) return null;

            // 拆成简单命令（PowerShell 用 ; 与 | 分隔）
            var segments = ShellLexer.SplitSegments(script);
            CommandRisk worst = CommandRisk.Safe;
            foreach (var seg in segments)
            {
                var r = JudgePowerShellSegment(seg, cwd, home);
                if (r.Rank > worst.Rank) worst = r;
            }
            return worst;
        }

        // ---------- cmd 删除命令 ----------
        if (DeleteCommands.Contains(cmd))
        {
            // del/erase 不需要 -r 就删（不像 rm）
            // rd/rmdir 只有 /s 才递归
            bool recursive = cmd is "del" or "erase"
                             || argv.Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase))
                             || argv.Any(a => a.Equals("/q", StringComparison.OrdinalIgnoreCase) && false);

            if (!recursive) return CommandRisk.Safe;

            var targets = Operands(argv);
            return WorstOfTargets(targets, cwd, home, "删除");
        }

        // ---------- format ----------
        if (cmd == "format")
        {
            // format C: /q /fs:ntfs
            var targets = Operands(argv).Where(t => !t.StartsWith('/')).ToList();
            return WorstOfTargets(targets, cwd, home, "格式化");
        }

        // ---------- diskpart ----------
        if (cmd == "diskpart")
        {
            // diskpart /s script.txt 或交互式 → 无法确定
            return CommandRisk.Uncertain(
                "diskpart 目标无法确定",
                "diskpart 通过脚本或交互操作磁盘，命令行判定看不穿");
        }

        // ---------- cipher（/w 擦除空闲空间） ----------
        if (cmd == "cipher")
        {
            var w = argv.FirstOrDefault(a => a.StartsWith("/w", StringComparison.OrdinalIgnoreCase));
            if (w is null) return CommandRisk.Safe;
            var t = w.Length > 2 && w[2] == ':' ? w[3..] : null;
            return t is null
                ? CommandRisk.Uncertain("cipher /w 目标无法确定", "请在 /w: 后写明路径")
                : WorstOfTargets(new List<string> { t }, cwd, home, "擦除");
        }

        // ---------- ACL ----------
        if (AclCommands.Contains(cmd))
        {
            var recursive = argv.Any(a => a.Equals("/t", StringComparison.OrdinalIgnoreCase))
                            || argv.Any(a => a.Equals("/r", StringComparison.OrdinalIgnoreCase))
                            || argv.Any(a => a.Equals("/s", StringComparison.OrdinalIgnoreCase));
            if (!recursive) return CommandRisk.Safe;
            return WorstOfTargets(Operands(argv).Where(t => !t.StartsWith('/')).ToList(),
                cwd, home, "递归改权限/属主");
        }

        // ---------- 注册表 / 引导 ----------
        if (SystemCommands.Contains(cmd))
        {
            return CommandRisk.Uncertain(
                $"{cmd} 属于系统级操作",
                "会修改注册表或引导配置，影响面大，请确认");
        }

        return null;
    }

    private static CommandRisk JudgePowerShellSegment(
        List<string> seg, string cwd, string? home)
    {
        if (seg.Count == 0) return CommandRisk.Safe;

        var cmdlet = seg[0].ToLowerInvariant();
        if (!DangerCmdlets.Contains(cmdlet)) return CommandRisk.Safe;

        // -Recurse / -Force / -Path / -LiteralPath 的参数
        var recursive = seg.Any(a =>
            a.Equals("-recurse", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-r", StringComparison.OrdinalIgnoreCase));

         // 删除类：默认递归（Remove-Item 对目录需要 -Recurse，但保守起见都判定）
         bool needsTargetJudge = cmdlet is "remove-item" or "ri" or "rm" or "del" or "erase"
                                 or "rd" or "rmdir" or "clear-disk" or "clear-content" or "clc"
                                 || recursive;

         // 整盘操作按磁盘号寻址（-Number N），没有文件路径可判定，一律拦截
         if (cmdlet is "clear-disk" or "initialize-disk")
             return CommandRisk.Blocked($"PowerShell {cmdlet} 会擦除整个磁盘", string.Join(' ', seg));


        // 取 -Path / -LiteralPath 之后的值，或第一个位置参数
        var targets = new List<string>();
        for (int i = 1; i < seg.Count; i++)
        {
            var a = seg[i];
            if (a.Equals("-path", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-literalpath", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < seg.Count) targets.Add(seg[i + 1]);
                i++;
                continue;
            }
            if (a.StartsWith('-')) continue;
            targets.Add(a);
        }

        // PowerShell 的 $env:USERPROFILE 这类 → Uncertain
        if (targets.Any(t => t.Contains("$env:", StringComparison.OrdinalIgnoreCase)))
            return CommandRisk.Uncertain(
                "PowerShell 环境变量目标无法确定",
                "含 $env: 形式的路径，判定时无法求值");

        return WorstOfTargets(targets, cwd, home, $"PowerShell {cmdlet} ");
    }

    private static string? ExtractPowerShellScript(List<string> argv)
    {
        for (int i = 0; i < argv.Count; i++)
        {
            var a = argv[i].ToLowerInvariant();
            if (a is "-command" or "-c" or "-encodedcommand")
                return i + 1 < argv.Count ? argv[i + 1] : null;
            if (a is "-file" or "-f")
                return null;   // 执行脚本文件 → 不做静态判定（保守，交给权限询问）
        }
        return null;
    }

    /// <summary>非选项参数（Windows 风格：/x 与 -x 都算选项）</summary>
    private static List<string> Operands(List<string> argv)
    {
        var result = new List<string>();
        bool afterDoubleDash = false;
        foreach (var a in argv.Skip(1))
        {
            if (!afterDoubleDash && a == "--") { afterDoubleDash = true; continue; }
            if (!afterDoubleDash &&
                (a.StartsWith('/') && a.Length > 1 && !a.Contains(':')   // /s 是选项；/ 单独是根
                 || a.StartsWith('-')))
                continue;
            result.Add(a);
        }
        return result;
    }

    private static CommandRisk WorstOfTargets(
        List<string> targets, string cwd, string? home, string verb)
    {
        CommandRisk worst = CommandRisk.Safe;
        foreach (var t in targets)
        {
            var res = PathNormalizer.Resolve(t, cwd, home);
            switch (res)
            {
                case PathResolution.Unknown u:
                    if (worst.Rank < 1)
                        worst = CommandRisk.Uncertain($"{verb}的目标无法确定", u.Reason);
                    break;

                case PathResolution.Ok ok:
                    if (IsDangerousWindowsPath(ok.Path, cwd, home, out var why))
                        return CommandRisk.Blocked($"{verb}命中{why}", ok.Path);
                    break;
            }
        }
        return worst;
    }

    /// <summary>Windows 高危路径判定。</summary>
    private static bool IsDangerousWindowsPath(
        string path, string cwd, string? home, out string why)
    {
        why = "";

        // ① 根盘符：C:\ 或 C:
        if (path.Length <= 3 && path.Length >= 2 && path[1] == ':')
        {
            why = "根盘符";
            return true;
        }
        if (path == "\\" || path == "/")
        {
            why = "根目录";
            return true;
        }

        // ② 家目录
        if (!string.IsNullOrEmpty(home) && PathNormalizer.PathEquals(path, home))
        {
            why = "家目录";
            return true;
        }

        // ③ 工作目录或其上级
        if (PathNormalizer.IsSameOrAncestor(cwd, path))
        {
            why = "当前工作目录或其上级";
            return true;
        }

        // ④ 系统目录（保守：Windows/System32 单独判定为危险）
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(sysRoot))
        {
            if (PathNormalizer.PathEquals(path, sysRoot) ||
                PathNormalizer.PathEquals(path, Path.Combine(sysRoot, "System32")))
            {
                why = "系统目录";
                return true;
            }
        }

        // ⑤ 物理磁盘
        if (path.Contains("physicaldrive", StringComparison.OrdinalIgnoreCase))
        {
            why = "物理磁盘";
            return true;
        }

        return false;
    }
}
