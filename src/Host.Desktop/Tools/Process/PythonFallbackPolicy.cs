using Host.Desktop.Tools.Process;

namespace Host.Desktop.Tools;

/// <summary>
/// need21-B04: Python 回退策略（need17-07 语义保留）。
/// runner=python 时集中探测可用性；不可用则统一经 PythonAvailabilityGate 强拒绝，
/// 调用方（ExecCommandTool）不再内联探测分支。
/// </summary>
public static class PythonFallbackPolicy
{
    public static (CommandRunnerProbe.ProbeResult? Probe, ToolResult? Fail) ResolveProbe(
        ToolContext context,
        string runner,
        System.Diagnostics.Stopwatch sw
    )
    {
        if (PythonAvailabilityGate.ShouldReject(runner, context.PythonAvailabilityOverride))
        {
            return (null, PythonAvailabilityGate.Fail(sw.ElapsedMilliseconds));
        }
        CommandRunnerProbe.ProbeResult probe =
            context.PythonProbeOverride != null
                ? context.PythonProbeOverride()
                : CommandRunnerProbe.ProbePython(configuredPath: context.ConfiguredPythonPath);
        if (!probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
        {
            return (null, PythonAvailabilityGate.Fail(sw.ElapsedMilliseconds));
        }
        return (probe, null);
    }
}
