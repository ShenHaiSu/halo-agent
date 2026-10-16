using System;

namespace Host.Desktop.Core.Models;

 /// <summary>need06-02：前置提示词配置文档（全局 config/prompt.json，唯一事实源）。</summary>
 public class PromptConfigDocument
{
    public int Version { get; set; } = 1;
    public bool EnableHaloPrefix { get; set; } = true;
    public bool EnableIdentity { get; set; } = true;
    public bool EnableRuntime { get; set; } = true;
    public bool EnableCmdPython { get; set; } = true;
    public bool EnableToolGuide { get; set; } = true;
    public bool EnableEphemeral { get; set; } = true;
    public bool EnableCustom { get; set; } = false;
    public string CustomPlacement { get; set; } = "tail";
    public string CustomText { get; set; } = string.Empty;
    public string IdentityText { get; set; } = PromptDefaults.IdentityText;
    public string RuntimeTemplate { get; set; } = PromptDefaults.RuntimeTemplate;
    public string CmdPythonText { get; set; } = PromptDefaults.CmdPythonText;
    public string ToolGuideTemplate { get; set; } = PromptDefaults.ToolGuideTemplate;
    public string EphemeralTemplate { get; set; } = PromptDefaults.EphemeralTemplate;
    public string Scope { get; set; } = "global";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class PromptDefaults
{
    public const int MaxBlockChars = 8000;
    public const int MaxAssembledChars = 32000;

    public static readonly string IdentityText =
        "You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.";

    public static readonly string RuntimeTemplate =
        "## 1. Runtime & Environment Facts\n" +
        "- Host OS: {{OsVersion}} (Architecture: {{OsArch}})\n" +
        "- Active Workspace Name: {{WorkspaceName}}\n" +
        "- Active Workspace Root Path: {{WorkspaceRoot}}\n" +
        "- Default Command Shell: `cmd.exe` (Windows Command Prompt)";

    public static readonly string CmdPythonText =
        "## 2. Command Execution Strategy (CRITICAL RULE)\n" +
        "Because PowerShell versions vary drastically between Windows 7 (PowerShell 2.0) and Windows 10/11 (PowerShell 5.1+), you MUST follow this strict execution priority when using `exec_command`:\n" +
        "1. **FIRST CHOICE (`runner: \"cmd\"`)**: Always default to standard Windows `cmd.exe` commands (`dir`, `type`, `findstr`, `copy`, `move`, `del`, `mkdir`, `where`, `tasklist`, `dotnet`, `node`, `git`, etc.) for shell operations and project builds.\n" +
        "2. **SECOND CHOICE (`runner: \"python\"`)**: When you need complex text processing, regex transformations, JSON/XML manipulation, or multi-step data calculation that is awkward in `cmd.exe`, use `runner: \"python\"` to run inline Python code or `.py` scripts.\n" +
        "3. **AVOID PowerShell**: Do NOT invoke `powershell` or `pwsh` unless the user explicitly asks you to debug a `.ps1` script.";

    public static readonly string ToolGuideTemplate =
        "## 3. Standard Tool Usage Guidelines\n" +
        "- Before modifying an existing file, ALWAYS read the relevant section first using `read_file`.\n" +
        "- For targeted modifications to existing files, prefer `edit_file` (providing enough surrounding lines in `old_string` for a unique match) instead of overwriting the entire file with `write_file`.\n" +
        "- Use `list_dir` and `grep_search` to explore the workspace efficiently rather than spawning unnecessary shell processes.\n" +
        "- All relative paths passed to tools resolve against the Active Workspace Root Path (`{{WorkspaceRoot}}`).";

    public static readonly string EphemeralTemplate =
        "## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n" +
        "- Current Date: {{CurrentDate}}\n" +
        "- Detected Python Runtime: {{PythonStatus}}\n" +
        "- SessionHash: 由网关头携带,此处不重复";

    public static PromptConfigDocument CreateDefault() => new() { UpdatedAtUtc = DateTime.UtcNow };
}

public sealed class PromptBlockPreview
{
    public string BlockId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string RenderedText { get; set; } = string.Empty;
    public int EstimatedTokens { get; set; }
    public bool IsStable { get; set; }
}

public sealed class PromptPreviewDto
{
    public string AssembledText { get; set; } = string.Empty;
    public List<PromptBlockPreview> Blocks { get; set; } = new();
    public int TotalEstimatedTokens { get; set; }
    public int StableEstimatedTokens { get; set; }
    public string PrefixHash { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public List<string> UnknownVariables { get; set; } = new();
    public bool IsDefault { get; set; }
    public string? FallbackReason { get; set; }
}

/// <summary>need06-02：保存校验失败异常，定位到块。</summary>
public sealed class PromptConfigValidationException : Exception
{
    public string BlockId { get; }

    public PromptConfigValidationException(string blockId, string message)
        : base(message)
    {
        BlockId = blockId ?? string.Empty;
    }
}
