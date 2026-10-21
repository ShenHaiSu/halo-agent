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
        "## 1. Runtime & Environment Facts\n"
        + "- Host OS: {{OsVersion}} (Architecture: {{OsArch}})\n"
        + "- Active Workspace Name: {{WorkspaceName}}\n"
        + "- Active Workspace Root Path: {{WorkspaceRoot}}\n"
        + "- Default Command Shell: `cmd.exe` (Windows Command Prompt)";

    // NEED17-07 预留拼接点：CmdPythonText 文案冻结不动；无 Python 时的条件 warning 行由 07 在 BuildCore 渲染后追加（见 SystemPromptBuilder）。
    public static readonly string CmdPythonText =
        "## 2. Command Execution Strategy (CRITICAL RULE)\n"
        + "Because PowerShell versions vary drastically between Windows 7 (PowerShell 2.0) and Windows 10/11 (PowerShell 5.1+), you MUST follow this strict execution priority when using `exec_command`:\n"
        + "1. **FIRST CHOICE (`runner: \"cmd\"`)**: Always default to standard Windows `cmd.exe` commands (`dir`, `type`, `findstr`, `copy`, `move`, `del`, `mkdir`, `where`, `tasklist`, `dotnet`, `node`, `git`, etc.) for shell operations and project builds.\n"
        + "2. **SECOND CHOICE (`runner: \"python\"`)**: When you need complex text processing, regex transformations, JSON/XML manipulation, or multi-step data calculation that is awkward in `cmd.exe`, use `runner: \"python\"` to run inline Python code or `.py` scripts.\n"
        + "3. **AVOID PowerShell**: Do NOT invoke `powershell` or `pwsh` unless the user explicitly asks you to debug a `.ps1` script.";

    /// <summary>need17-06 §6.1：ToolGuideTemplate 冻结英文（逐字，12 行；{{WorkspaceRoot}} 保留变量）。只教选型与红线，不教调用语法。</summary>
    public static readonly string ToolGuideTemplate =
        "## 3. Standard Tool Usage Guidelines\n"
        + "- Before modifying ANY file, ALWAYS read the exact section with `read_file` first. NEVER invent `old_string` from memory.\n"
        + "- `old_string` MUST be a verbatim copy-paste from the latest `read_file` output (including indentation). Add 2-5 surrounding lines to make it unique.\n"
        + "- Small partial edit with a clear anchor: `edit_file`. Huge/CRLF/Tab files, or after any `OLD_STRING_NOT_FOUND`/`ANCHOR_AMBIGUOUS`: `edit_lines(offset,limit)`.\n"
        + "- NEVER use `write_file` for partial edits. `write_file` creates new files or fully overwrites (requires `allow_overwrite:true`); truncated content is rejected.\n"
        + "- `edit_lines` requires explicit `limit` (lines to REPLACE; 0 = insert before offset). Line numbers are 1-based, identical to `read_file`.\n"
        + "- If `edit_file` reports `OLD_STRING_NOT_FOUND` or `ANCHOR_AMBIGUOUS`, do NOT retry the same anchor. Follow the `hint` (use the suggested `edit_lines` line range).\n"
        + "- Locate first: `list_dir` for structure, `grep_search` for content. Do NOT `cat/type` files via `exec_command` to view them.\n"
        + "- `exec_command` runs in the workspace root (stdout+stderr merged, tail kept). Prefer `cmd`; `python` only for text processing/calculation. Never PowerShell unless asked.\n"
        + "- Large outputs are truncated (head+tail) with full text spilled to a log file path in the result. Page with `read_file(offset,limit)` or narrow `grep_search`; never re-run a broad dump.\n"
        + "- All relative paths resolve against `{{WorkspaceRoot}}`.\n"
        + "- For risky/large edits, call with `dry_run:true` first to preview the diff, then repeat without it.";

    public static readonly string EphemeralTemplate =
        "## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n"
        + "- Current Date: {{CurrentDate}}\n"
        + "- Detected Python Runtime: {{PythonStatus}}\n"
        + "- SessionHash: 由网关头携带,此处不重复";

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
