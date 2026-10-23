using Host.Desktop.Core.Models;

namespace Host.Desktop.Tools;

/// <summary>need13/need21-B05：轮首策略快照（原角色工厂文件内定义整体搬迁，语义零变化）。</summary>
public sealed class SubPolicySnapshot
{
    public bool SubAgentEnabled { get; init; } = true;
    public int SubMaxParallel { get; init; } = 3;
    public int SubMaxIterations { get; init; } = 15;
    public bool SubAllowExecCommand { get; init; } = true;
    public bool SubAllowFileWrite { get; init; } = true;
    public bool SubAllowSkillWrite { get; init; } = false;
    public bool SubAllowMemoryWrite { get; init; } = false;

    public SubPolicySnapshot() { }

    public SubPolicySnapshot(AgentConfigDocument cfg)
    {
        if (cfg == null)
            return;
        SubAgentEnabled = cfg.SubAgentEnabled;
        SubMaxParallel = cfg.SubMaxParallel;
        SubMaxIterations = cfg.SubMaxIterations;
        SubAllowExecCommand = cfg.SubAllowExecCommand;
        SubAllowFileWrite = cfg.SubAllowFileWrite;
        SubAllowSkillWrite = cfg.SubAllowSkillWrite;
        SubAllowMemoryWrite = cfg.SubAllowMemoryWrite;
    }
}
