namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// 高危命令判定的唯一入口。
///
/// ============ 判定流水线 ============
///
///  ① 词法切分
///     按 ; &amp;&amp; || | &amp; 换行 → 简单命令段
///     每段内按空白切词（处理引号与转义）
///
///  ② 字面变量收集
///     T=/; rm -rf $T → 收集 T=/，展开后能正确判定
///
///  ③ 逐段判定 → 取最严重
///     每段：剥前缀 → 按命令分派 → 解析目标路径 → 规范化比较
///
///  ④ 递归
///     sh -c '...' / cmd /c '...' / powershell -Command '...'
///     递归判定脚本文本（深度上限 3）
///
/// ============ 边界（必须写进 UI 文案） ============
/// 这是**启发式识别**，不是安全边界。看不穿的：
///   - 变量间接（脚本里循环赋值）
///   - 命令替换 $(...) 的内部
///   - xargs 的目标（来自 stdin）
///   - 解释器读文件执行（bash script.sh 的内容）
///   - 执行时才产生的值
/// 真正的隔离需要内核机制（沙箱/容器/受限账户）。
/// </summary>
public static class DangerousCommandInspector
{
    public static CommandRisk Inspect(string command, InspectorOptions opts)
        => InspectInner(command, opts, depth: 0);

    internal static CommandRisk InspectInner(string command, InspectorOptions opts, int depth)
    {
        if (string.IsNullOrWhiteSpace(command)) return CommandRisk.Safe;
        if (depth > opts.MaxDepth) return CommandRisk.Safe;

        var segments = ShellLexer.SplitSegments(command);
        if (segments.Count == 0) return CommandRisk.Safe;

        var vars = ShellLexer.CollectAssignments(segments, opts.Cwd, opts.Home);

        CommandRisk worst = CommandRisk.Safe;
        foreach (var seg in segments)
        {
            var argv = seg.Select(w => ShellLexer.ExpandVariables(w, vars)).ToList();
            var risk = CommandRules.JudgeSegment(argv, opts, depth);

            if (risk.Rank > worst.Rank) worst = risk;
            if (worst.Rank == 2) break;   // Blocked 最高，不用再看
        }
        return worst;
    }
}
