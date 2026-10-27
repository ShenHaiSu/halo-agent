namespace HaloAgent.Desktop.Engine;

/// <summary>引擎进程启动参数（生命周期内不可变）。</summary>
public sealed class EngineProcessOptions
{
    public string EngineExePath { get; init; } = "";
    public string PipeName { get; init; } = "";
    public string DataDir { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string LogDir { get; init; } = "";
    public bool Headless { get; init; }
    public string? ExtraArgs { get; init; }

    public static string NewPipeName() =>
        $"haloagent-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];
}
