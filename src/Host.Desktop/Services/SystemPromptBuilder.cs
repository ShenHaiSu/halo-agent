using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Services;

/// <summary>
/// Stage 06 动态系统提示词引擎：将 Host OS/Arch、工作区、cmd/python 优先铁律与工具指南
/// 动态注入每次 ReAct 推理的 System 指令。模板严格对齐 docs/develop/stage06/02 文档。
/// </summary>
public sealed class SystemPromptBuilder
{
    /// <summary>
    /// 复用 CommandRunnerProbe 探测本机 Python 状态（带版本号），供 System Prompt 与 UI 状态栏共享。
    /// 成功示例："3.11.9 (C:\Python311\python.exe)"；未检出返回 "(未检测到 Python)" 风格文本。
    /// 永不抛异常，探测失败即返回兜底文本。
    /// </summary>
    public static string GetPythonStatus()
    {
        try
        {
            var probe = CommandRunnerProbe.ProbePython();
            if (probe == null || !probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
            {
                return "(Python not detected - fall back to cmd.exe)";
            }

            string version = TryGetPythonVersion(probe.ExePath, probe.LauncherArgsPrefix);
            string display = string.IsNullOrWhiteSpace(version)
                ? probe.DisplayName
                : $"{version} ({probe.ExePath})";
            return display;
        }
        catch
        {
            return "(Python not detected - fall back to cmd.exe)";
        }
    }

    /// <summary>
    /// 动态拼装 Markdown 系统提示词模板。
    /// </summary>
    public static Task<string> BuildAsync(WorkspaceInfo workspace, string? pythonStatusOverride = null)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));

        string osVersion = RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.VersionString;
        string arch = RuntimeInformation.OSArchitecture.ToString();
        string wsName = string.IsNullOrWhiteSpace(workspace.Name) ? "workspace" : workspace.Name.Trim();
        string wsRoot = string.IsNullOrWhiteSpace(workspace.RootPath) ? Environment.CurrentDirectory : workspace.RootPath;
        string pythonStatus = string.IsNullOrWhiteSpace(pythonStatusOverride) ? GetPythonStatus() : pythonStatusOverride.Trim();
        string today = DateTime.Now.ToString("yyyy-MM-dd");

        string prompt =
            "You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.\n" +
            "\n" +
            "## 1. Runtime & Environment Facts\n" +
            $"- Host OS: {osVersion} (Architecture: {arch})\n" +
            $"- Active Workspace Name: {wsName}\n" +
            $"- Active Workspace Root Path: {wsRoot}\n" +
            "- Default Command Shell: `cmd.exe` (Windows Command Prompt)\n" +
            "\n" +
            "## 2. Command Execution Strategy (CRITICAL RULE)\n" +
            "Because PowerShell versions vary drastically between Windows 7 (PowerShell 2.0) and Windows 10/11 (PowerShell 5.1+), you MUST follow this strict execution priority when using `exec_command`:\n" +
            "1. **FIRST CHOICE (`runner: \"cmd\"`)**: Always default to standard Windows `cmd.exe` commands (`dir`, `type`, `findstr`, `copy`, `move`, `del`, `mkdir`, `where`, `tasklist`, `dotnet`, `node`, `git`, etc.) for shell operations and project builds.\n" +
            "2. **SECOND CHOICE (`runner: \"python\"`)**: When you need complex text processing, regex transformations, JSON/XML manipulation, or multi-step data calculation that is awkward in `cmd.exe`, use `runner: \"python\"` to run inline Python code or `.py` scripts.\n" +
            "3. **AVOID PowerShell**: Do NOT invoke `powershell` or `pwsh` unless the user explicitly asks you to debug a `.ps1` script.\n" +
            "\n" +
            "## 3. Standard Tool Usage Guidelines\n" +
            "- Before modifying an existing file, ALWAYS read the relevant section first using `read_file`.\n" +
            "- For targeted modifications to existing files, prefer `edit_file` (providing enough surrounding lines in `old_string` for a unique match) instead of overwriting the entire file with `write_file`.\n" +
            "- Use `list_dir` and `grep_search` to explore the workspace efficiently rather than spawning unnecessary shell processes.\n" +
            $"- All relative paths passed to tools resolve against the Active Workspace Root Path (`{wsRoot}`).\n";

        // need05-02: stable/ephemeral split.
        // 易变行(日期/Python状态)只出现在尾部 ## 4. Session Ephemeral. 对调用方仍返回一个string(零破坏).
        prompt +=
            "\n" +
            "## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n" +
            $"- Current Date: {today}\n" +
            $"- Detected Python Runtime: {pythonStatus}\n" +
            "- SessionHash: 由网关头携带,此处不重复\n";

        return Task.FromResult(prompt);
    }

    private static string TryGetPythonVersion(string exePath, string launcherPrefix)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = exePath;
            string args = string.IsNullOrWhiteSpace(launcherPrefix)
                ? "--version"
                : launcherPrefix.Trim() + " --version";
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            CommandRunnerProbe.ApplyStandardEnv(process.StartInfo);
            if (!process.Start())
            {
                return string.Empty;
            }

            string stdout = process.StandardOutput.ReadToEnd() ?? string.Empty;
            string stderr = process.StandardError.ReadToEnd() ?? string.Empty;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { /* ignore */ }
                return string.Empty;
            }

            string combined = (stdout + "\n" + stderr).Trim();
            // 期望形如 "Python 3.11.9"：取首个含 Python 的行并截断
            foreach (string rawLine in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Length > 64 ? line[..64] : line;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
