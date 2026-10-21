namespace Host.Desktop.Tools;

/// <summary>
/// need21-B04: 工具策略集中（ExecCommand 白名单/超时/Python 回退策略收敛于此；
/// need17-07 Python 回退语义不变，逻辑见 PythonFallbackPolicy）。
/// </summary>
public static class ToolPolicy
{
    public const int DefaultTimeoutMs = 60000;
    public const int MaxTimeoutMs = 300000;

    /// <summary>need22-P0 S2：command 长度上限 8K（字符）。</summary>
    public const int MaxCommandChars = 8192;

    /// <summary>need22-P0 S2：默认外部可执行白名单（可经 ToolContext.AllowedCommandsOverride / agent.json AllowedCommands 增补）。</summary>
    public static readonly string[] DefaultAllowedCommands =
    {
        "git",
        "node",
        "pnpm",
        "npm",
        "npx",
        "python",
        "py",
        "pip",
        "pip3",
        "dotnet",
        "uv",
        "cargo",
        "go",
    };

    /// <summary>need22-P0 S2：cmd 内建命令白名单（大小写不敏感；默认拒绝其余一切首 token）。</summary>
    public static readonly string[] CmdBuiltinCommands =
    {
        "echo",
        "dir",
        "type",
        "more",
        "where",
        "ver",
        "cd",
        "chdir",
        "mkdir",
        "md",
        "rmdir",
        "rd",
        "copy",
        "xcopy",
        "robocopy",
        "move",
        "ren",
        "rename",
        "ping",
        "tracert",
        "ipconfig",
        "tasklist",
        "timeout",
        "choice",
        "find",
        "findstr",
        "sort",
        "fc",
        "comp",
        "cls",
        "vol",
        "hostname",
        "whoami",
        "for",
        "if",
        "rem",
    };

    public const string PythonMissingMessage =
        "Error: Python runtime is not detected on this machine. "
        + "Please fall back to 'cmd' runner or ask the user to configure Python path in Settings.";
}

/// <summary>need21-B05: 角色注册（原角色工厂 ForMain/ForSub 整体搬迁，语义零变化；工厂类物理删除）。</summary>
public static class ToolRoleRegistries
{
    public static ToolRegistry ForMainRole(SubPolicySnapshot policy)
    {
        var r = new ToolRegistry(registerBuiltIns: true);
        r.Register(new BuiltIn.SpawnSubAgentTool(policy.SubAgentEnabled));
        return r;
    }

    public static ToolRegistry ForSubRole(SubPolicySnapshot policy)
    {
        var r = new ToolRegistry(registerBuiltIns: false);
        r.Register(new BuiltIn.ReadFileTool());
        r.Register(new BuiltIn.ListDirTool());
        r.Register(new BuiltIn.GrepSearchTool());
        r.Register(new BuiltIn.ViewImageTool());
        if (policy.SubAllowExecCommand)
            r.Register(new BuiltIn.ExecCommandTool());
        if (policy.SubAllowFileWrite)
        {
            r.Register(new BuiltIn.WriteFileTool());
            r.Register(new BuiltIn.EditFileTool());
            r.Register(new BuiltIn.EditLinesTool());
            r.Register(new BuiltIn.EditTool());
            r.Register(new BuiltIn.UndoEditTool());
        }
        r.Register(new BuiltIn.SkillTool());
        r.Register(new BuiltIn.MemoryTool());
        r.Register(new BuiltIn.GoalTool());
        r.Register(new BuiltIn.DisabledAskUserTool());
        r.Register(new BuiltIn.DisabledSpawnTool());
        return r;
    }
}
