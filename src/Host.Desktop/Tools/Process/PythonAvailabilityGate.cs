using Host.Desktop.Tools;

namespace Host.Desktop.Tools.Process;

/// <summary>
/// need17-07：Python 缺失秒拒静态门面。冻结文案唯一事实源（docs/need17/07-python-fallback.md §7.1，逐字）。
/// 探测逻辑不动：本类只做“是否拒绝”判定 + 文案装配，不调用 <see cref="CommandRunnerProbe"/>。
/// </summary>
public static class PythonAvailabilityGate
{
    /// <summary>结构化错误码（need15 码约定：Error 首 token 即码）。</summary>
    public const string FailCode = "PYTHON_NOT_AVAILABLE";

    /// <summary>冻结第 1 段（码后缀体，不含码前缀；FailCode 会自动补码前缀）。</summary>
    public const string Line1 = "当前电脑环境中没有检测到或者没有安装python，如果必须py，请引导用户前往官网进行下载和安装配置后重启HaloAgent。";

    /// <summary>冻结第 2 段（同一 Error 内换行追加，不另起码）。</summary>
    public const string Line2 =
        "已收到 runner:'python'，本次未执行。请改用 runner:'cmd' 重试（如 {\"command\":\"dir /b\",\"runner\":\"cmd\"}）；或请用户安装 Python 后重试。";

    /// <summary>
    /// 全仓库唯一中文 Hint（HintForTool("exec_command", "PYTHON_NOT_AVAILABLE") 返回此值，逐字断言）。
    /// 注意首字符为空格，与既有 HintForTool 返回风格一致。
    /// </summary>
    public const string HintText =
        " Hint: 本机无 Python，请用 runner:'cmd' 继续；需要 Python 请按上方指引安装后重启 HaloAgent。";

    /// <summary>
    /// 冻结拒绝全文（含码前缀 + 第 1 段 + 换行 + 第 2 段）。
    /// 可直接用作 Error，也可传入 <see cref="ToolResult.FailCode"/>（码前缀自动去重，结果逐字相同）。
    /// </summary>
    public static string FrozenMessage() => $"{FailCode}：{Line1}\n{Line2}";

    /// <summary>全仓库唯一中文 Hint（<see cref="HintText"/> 的方法形态，供 HintForTool 直接返回）。</summary>
    public static string Hint() => HintText;

    /// <summary>
    /// 是否应秒拒本次调用。仅当 runner == "python"（大小写不敏感，前后空格容忍）
    /// 且有效快照缺失（<paramref name="snapshotOverride"/> ?? <see cref="PythonAvailabilitySnapshot.Current"/>）
    /// 时返回 true；runner 为 cmd/其它/空一律返回 false（不受快照影响）。
    /// 快照为 null 时按缺失处理（fail-closed）。
    /// </summary>
    public static bool ShouldReject(
        string? runner,
        PythonAvailabilitySnapshot? snapshotOverride = null
    )
    {
        if (!string.Equals(runner?.Trim(), "python", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        PythonAvailabilitySnapshot? snapshot =
            snapshotOverride ?? PythonAvailabilitySnapshot.Current;
        if (snapshot is null)
            return true;
        // need17联调：从未探测（Unknown，ProbedAtUtc==MinValue）时不预拒，留给本次 Probe 双保险判定，避免单测/首启误杀。
        if (snapshot.ProbedAtUtc == DateTimeOffset.MinValue)
            return false;
        return !snapshot.Found;
    }

    /// <summary>
    /// 秒拒失败结果（ErrorCode=PYTHON_NOT_AVAILABLE，Error=<see cref="FrozenMessage"/>，ExitCode=-1）。
    /// 秒拒路径调用此方法：不计时归因、不建进程、不落脚本（调用方直接 return）。
    /// </summary>
    public static ToolResult Fail(long durationMs = 0) =>
        ToolResult.FailCode(FailCode, $"{Line1}\n{Line2}", durationMs: durationMs);
}
