using Host.Desktop.Core.Models;
using Host.Desktop.Tools.BuiltIn;

namespace Host.Desktop.Tools;

/// <summary>
/// need13：主/子角色过滤注册工厂（03 §3.2）。
/// 主包独占 spawn 真身；子包按策略裁剪（exec/file 可配，skill/memory 只读，ask/spawn 禁用桩）。
/// </summary>
public static class ToolRegistryFactory
{
    public static ToolRegistry ForMain(SubPolicySnapshot policy)
    {
        var r = new ToolRegistry(registerBuiltIns: true);
        r.Register(new SpawnSubAgentTool(policy.SubAgentEnabled));
        return r;
    }

    public static ToolRegistry ForSub(SubPolicySnapshot policy)
    {
        var r = new ToolRegistry(registerBuiltIns: false);
        r.Register(new ReadFileTool());
        r.Register(new ListDirTool());
        r.Register(new GrepSearchTool());
        r.Register(new ViewImageTool());
        if (policy.SubAllowExecCommand)
            r.Register(new ExecCommandTool());
        if (policy.SubAllowFileWrite)
        {
            r.Register(new WriteFileTool());
            r.Register(new EditFileTool());
            r.Register(new EditLinesTool());
            r.Register(new EditTool());
            r.Register(new UndoEditTool());
        }
        // skill/memory 真身（写动作由 SkillToolShared 内鉴权拦截，见 03 §3.3）
        r.Register(new SkillTool());
        r.Register(new MemoryTool());
        // need16：goal_tool 子只读（同一 GoalTool 靠 AgentRole 内鉴权；description 已含只读注记）
        r.Register(new GoalTool());
        // ask/spawn 禁用桩
        r.Register(new DisabledAskUserTool());
        r.Register(new DisabledSpawnTool());
        return r;
    }
}

/// <summary>need13：轮首策略快照（05 §3：Orchestrator 每轮一次拷贝，本轮内 spawn/skill/memory 判定全用它）。</summary>
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
