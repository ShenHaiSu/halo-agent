namespace Host.Desktop.Tools.Process;

/// <summary>
/// need17-07：Python 可用性进程级快照（线程安全）。
/// 探测逻辑不动：内部复用 <see cref="CommandRunnerProbe.ProbePython()"/>（无参重载，
/// 命中其进程级缓存，无双重开销）。失败吞异常，<see cref="Current"/> 保持 Found=false。
/// </summary>
public sealed class PythonAvailabilitySnapshot
{
    /// <summary>是否检测到可用 Python（ExePath 非空才算 true，与 Exec 侧判定一致）。</summary>
    public bool Found { get; init; }

    /// <summary>可用解释器路径（含 py launcher 的 py.exe）；未检测到时为 null。</summary>
    public string? ExePath { get; init; }

    /// <summary>展示名（收敛自 ProbeResult.DisplayName）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>本次快照探测时间（UTC）。</summary>
    public DateTimeOffset ProbedAtUtc { get; init; }

    private static readonly object Sync = new();

    // 注意：不可用 = Unknown 做字段初始化（静态初始化顺序下 Unknown 尚为 null）；
    // _current 为 null 时 getter 回落到 Unknown。
    private static PythonAvailabilitySnapshot? _current;
    private static PythonAvailabilitySnapshot? _testOverride;

    /// <summary>启动前初始值：未探测 == 未检测到（fail-closed，秒拒先生效）。</summary>
    public static PythonAvailabilitySnapshot Unknown { get; } =
        new()
        {
            Found = false,
            ExePath = null,
            DisplayName = "(未检测到 Python)",
            ProbedAtUtc = DateTimeOffset.MinValue,
        };

    /// <summary>当前快照（测试注入期间返回注入值）。</summary>
    public static PythonAvailabilitySnapshot Current
    {
        get
        {
            lock (Sync)
            {
                return _testOverride ?? _current ?? Unknown;
            }
        }
        private set
        {
            lock (Sync)
            {
                _current = value;
            }
        }
    }

    /// <summary>
    /// 同步探测一次并替换 <see cref="Current"/>。永不抛异常；
    /// 探测异常时写入 Found=false 快照（含本次 ProbedAtUtc）。
    /// </summary>
    public static void Refresh()
    {
        try
        {
            CommandRunnerProbe.ProbeResult probe = CommandRunnerProbe.ProbePython();
            Current = new PythonAvailabilitySnapshot
            {
                Found = probe.Found && !string.IsNullOrWhiteSpace(probe.ExePath),
                ExePath = probe.ExePath,
                DisplayName = probe.DisplayName,
                ProbedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        catch
        {
            Current = new PythonAvailabilitySnapshot
            {
                Found = false,
                ExePath = null,
                DisplayName = "(未检测到 Python)",
                ProbedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>单测注入快照（<see cref="Current"/> 在清除前一律返回注入值）。</summary>
    public static void OverrideForTests(PythonAvailabilitySnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (Sync)
        {
            _testOverride = snapshot;
        }
    }

    /// <summary>清除单测注入，恢复真实快照（每个用例 try/finally 调用）。</summary>
    public static void ClearOverrideForTests()
    {
        lock (Sync)
        {
            _testOverride = null;
        }
    }
}
