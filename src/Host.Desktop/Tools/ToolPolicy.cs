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
