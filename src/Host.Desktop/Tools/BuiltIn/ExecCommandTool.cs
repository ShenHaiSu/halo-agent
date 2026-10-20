using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// exec_command：默认 cmd、次选 python 的命令执行引擎。
/// JobObject 进程树管控 + 异步字节流读取 + SmartDecoder + ToolOutputGuard。
/// need22-P0 S2：首 token 白名单（可配）、command 8K 上限、workdir 强制 ValidateWrite、
/// ComSpec 固定、超时默认 60s/上限 300s 且 timeout_ms&lt;=0 非法、全命令审计日志。
/// </summary>
public sealed class ExecCommandTool : IToolHandler
{
    public const int DefaultTimeoutMs = 60000;
    public const int MaxTimeoutMs = 300000;

    /// <summary>need22-P0 S2：command 长度上限 8K（字符）。</summary>
    public const int MaxCommandChars = 8192;

    /// <summary>need22-P0 S2：默认外部可执行白名单（可经 ToolContext.AllowedCommandsOverride / agent.json AllowedCommands 增补）。</summary>
    public static readonly string[] DefaultAllowedCommands =
    {
        "git", "node", "pnpm", "npm", "npx", "python", "py", "pip", "pip3",
        "dotnet", "uv", "cargo", "go",
    };

    /// <summary>need22-P0 S2：cmd 内建命令白名单（大小写不敏感；默认拒绝其余一切首 token）。</summary>
    public static readonly string[] CmdBuiltinCommands =
    {
        "echo", "dir", "type", "more", "where", "ver", "cd", "chdir",
        "mkdir", "md", "rmdir", "rd", "copy", "xcopy", "robocopy",
        "move", "ren", "rename", "ping", "tracert", "ipconfig", "tasklist",
        "timeout", "choice", "find", "findstr", "sort", "fc", "comp",
        "cls", "vol", "hostname", "whoami", "for", "if", "rem",
    };

    private static readonly Regex AbsPathProbe =
        new(@"(?i)(?<![A-Za-z0-9_])([A-Za-z]:[\\/]|\\\\[A-Za-z0-9])", RegexOptions.Compiled);

    private static readonly Regex RedirectProbe =
        new(@"(^|[^\d\w])\d?\s*>>?\s*(""[^""]+""|\S+)", RegexOptions.Compiled);

    public const string PythonMissingMessage =
        "Error: Python runtime is not detected on this machine. " +
        "Please fall back to 'cmd' runner or ask the user to configure Python path in Settings.";

    public string Name => "exec_command";

    public string Description =>
        "Execute a command or inline script in the workspace. " +
        "IMPORTANT FOR WIN7/WIN10/WIN11 COMPATIBILITY: Always prefer 'cmd' (default Windows Command Prompt) " +
        "for shell operations, file management, and build commands. " +
        "Secondarily prefer 'python' ('py') for complex text processing, calculation, or scripting. " +
        "Avoid PowerShell because PowerShell versions differ drastically across Windows 7 (PS 2.0) and Windows 10/11. " +
        "Only whitelisted executables may run (git/node/pnpm/npm/python/pip/dotnet + safe cmd builtins); others are rejected. " +
        "Output keeps the TAIL (last lines) on truncation; full text beyond the budget is spilled to a log file. " +
        "Large outputs are truncated to head+tail within a fixed budget; the full text is spilled to a log file path in the result. Use read_file(offset,limit) or grep_search to page, never re-run a broad command to dump everything.";

    public ToolDefinition GetDefinition()
    {
        string desc = Description;
        const string NoPyNote = " (注意：当前环境未检测到 Python，runner:'python' 不可用，请使用 'cmd')";
        string runnerNote = PythonAvailabilitySnapshot.Current.Found ? string.Empty : NoPyNote;
        if (runnerNote.Length > 0) { desc += NoPyNote; }
        return new ToolDefinition(Name, desc,
        "{\"type\":\"object\",\"properties\":{"
        + "\"command\":{\"type\":\"string\",\"description\":\"Full command line (runner cmd) or Python code/script args (runner python). Runs in workspace root; stdout+stderr merged. Max 8192 chars; cmd runner only allows whitelisted executables.\"},"
        + "\"runner\":{\"type\":\"string\",\"enum\":[\"cmd\",\"python\"],\"default\":\"cmd\",\"description\":\"cmd first. python for text processing. No PowerShell." + runnerNote + "\"},"
        + "\"workdir\":{\"type\":\"string\",\"description\":\"Optional dir relative to workspace root. Defaults to workspace root. Must stay inside the workspace; missing dir fails fast.\"},"
        + "\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"default\":60000,\"maximum\":300000,\"description\":\"Optional timeout in ms. Must be >=1. Defaults 60000. Capped 300000.\"}"
        + "},\"required\":[\"command\"],\"additionalProperties\":false}");
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? tempScriptPath = null;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.CancellationToken);
            CancellationToken token = linkedCts.Token;

            // ---- 参数解析（need22-P0 S3：统一走 NormalizedJson 单次解析）----
            string command = string.Empty;
            string runner = "cmd";
            string? workdirArg = null;
            int? parsedTimeout = null; // null=缺省/非数字
            try
            {
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    command = args.GetString("command") ?? string.Empty;
                    runner = (args.GetString("runner") ?? "cmd").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(runner))
                    {
                        runner = "cmd";
                    }
                    workdirArg = args.GetString("workdir");
                    if (args.TryGetInt32("timeout_ms", out int parsed))
                    {
                        parsedTimeout = parsed;
                    }
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。请检查 command/runner/workdir/timeout_ms 字段后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                return ToolResult.Fail(
                    "缺少必需参数 'command'。cmd 示例：{\"command\": \"dir /b\"}；python 示例：{\"command\": \"print('hi')\", \"runner\": \"python\"}。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            if (runner != "cmd" && runner != "python")
            {
                return ToolResult.Fail(
                    $"不支持的 runner '{runner}'，仅支持 'cmd'（默认首选）与 'python'（次选）。请修正 runner 后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            // need22-P0 S2：长度上限 8K
            if (command.Length > MaxCommandChars)
            {
                return ToolResult.FailCode("COMMAND_TOO_LONG",
                    $"COMMAND_TOO_LONG：command 长 {command.Length} 字符，超过上限 {MaxCommandChars}。请拆小命令后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;

            // need22-P0 S2：workdir 强制 ValidateWrite（越界即 PATH_OUTSIDE_WORKSPACE）
            string workDir;
            {
                var (ok, err, full, _) = PathSafetyValidator.ValidateWrite(workspaceRoot, workdirArg);
                if (!ok)
                {
                    return ToolResult.FailCode(
                        PathSafetyValidator.Codes.OutsideWorkspace, err!,
                        durationMs: sw.ElapsedMilliseconds);
                }
                workDir = full;
            }
            if (!Directory.Exists(workDir))
            {
                return ToolResult.Fail(
                    $"工作目录不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, workDir)}'。请使用 read_file/list_dir 确认路径后重试（workdir 为空默认使用工作区根）。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            // need22-P0 S2：cmd runner 白名单 + 路径/重定向收敛（python runner 为代码形态，跳过 shell 校验）
            if (runner == "cmd")
            {
                ToolResult? gate = CheckCmdPolicy(command, workDir, workspaceRoot, context, sw);
                if (gate != null)
                {
                    return gate;
                }
            }

            // need22-P0 S2：超时默认 60s、上限 300s；timeout_ms<=0 非法（删除 0=无限语义）
            if (parsedTimeout.HasValue && parsedTimeout.Value <= 0)
            {
                return ToolResult.FailCode("INVALID_TIMEOUT",
                    $"INVALID_TIMEOUT：timeout_ms 非法（{parsedTimeout.Value}），必须 ≥1（默认 {DefaultTimeoutMs}，上限 {MaxTimeoutMs}）。不允许无限等待。",
                    durationMs: sw.ElapsedMilliseconds);
            }
            int cfgD = context.ExecDefaultTimeoutMsOverride is int d && d > 0 ? d : DefaultTimeoutMs;
            int cfgM = context.ExecMaxTimeoutMsOverride is int m && m > 0 ? m : MaxTimeoutMs;
            var (timeoutMs, _) = Host.Desktop.Core.Models.AgentTimeoutResolver.Resolve(parsedTimeout, cfgD, cfgM);
            if (timeoutMs <= 0)
            {
                timeoutMs = DefaultTimeoutMs;
            }

            // need22-P0 S2：审计日志（命令全文，脱敏后；长度截断防爆日志）
            try
            {
                var (scrubbed, _) = SecretScrubber.Scrub(command);
                string preview = scrubbed.Length > 500 ? scrubbed.Substring(0, 500) + "..." : scrubbed;
                Infrastructure.Logging.HaloLogger.Tool.Info(
                    $"exec_command audit (call {call.Id}) runner={runner} timeout={timeoutMs}ms workdir={PathSafetyValidator.ToDisplayPath(workspaceRoot, workDir)} cmd={preview}");
            }
            catch
            {
                // 审计失败不阻断执行
            }

            ProcessStartInfo psi;
            if (runner == "cmd")
            {
                if (!OperatingSystem.IsWindows())
                {
                    return ToolResult.Fail(
                        "runner 'cmd' 仅支持 Windows 平台。当前系统非 Windows，请改用 'python' runner。",
                        durationMs: sw.ElapsedMilliseconds);
                }

                psi = CommandRunnerProbe.BuildCmdStartInfo(command, workDir);
            }
            else
            {
                if (PythonAvailabilityGate.ShouldReject(runner, context.PythonAvailabilityOverride))
                {
                    return PythonAvailabilityGate.Fail(sw.ElapsedMilliseconds);
                }
                CommandRunnerProbe.ProbeResult probe = context.PythonProbeOverride != null
                    ? context.PythonProbeOverride()
                    : CommandRunnerProbe.ProbePython(configuredPath: context.ConfiguredPythonPath);
                if (!probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
                {
                    return PythonAvailabilityGate.Fail(sw.ElapsedMilliseconds);
                }

                string pythonArgs;
                if (LooksLikeScriptFileInvocation(command, workDir))
                {
                    pythonArgs = CommandRunnerProbe.BuildPythonArguments(probe, command);
                }
                else
                {
                    // 多行/内联代码 → 落盘为 UTF-8 临时脚本后 -u 执行，规避引号转义地狱
                    Directory.CreateDirectory(context.SessionTempDir);
                    tempScriptPath = Path.Combine(context.SessionTempDir, $"exec_{SanitizeId(call.Id)}.py");
                    await File.WriteAllTextAsync(tempScriptPath, command, new System.Text.UTF8Encoding(false), token)
                        .ConfigureAwait(false);
                    pythonArgs = CommandRunnerProbe.BuildPythonArguments(probe, "-u \"" + tempScriptPath + "\"");
                }

                psi = CommandRunnerProbe.BuildPythonStartInfo(probe.ExePath, pythonArgs, workDir);
            }

            // ---- 启动进程（JobObject 护航）----
            using var job = JobObjectManager.Create();
            using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = false };

            bool started;
            try
            {
                started = process.Start();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    $"进程启动失败：{ex.Message}。请检查命令与工作目录后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            if (!started)
            {
                return ToolResult.Fail("进程启动失败（Process.Start 返回 false）。", durationMs: sw.ElapsedMilliseconds);
            }

            job.Assign(process);

            // 异步原始字节读取（BaseStream 直读，避免编码死锁）
            Task<byte[]> stdoutTask = ReadStreamBytesAsync(process.StandardOutput.BaseStream, token);
            Task<byte[]> stderrTask = ReadStreamBytesAsync(process.StandardError.BaseStream, token);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeoutCts.CancelAfter(timeoutMs);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // 超时：关闭 Job 句柄即原子强杀整棵进程树，再兜底 Kill
                    job.Dispose();
                    TryKill(process);
                    string partial = await CollectPartialAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                    var (timeoutDisplay, timeoutSpill) = ToolOutputGuard.ApplyGuard(
                        partial, context.DataRoot, context.WorkspaceId, context.SessionId, call.Id);
                    string timeoutMsg = $"命令执行超时（{timeoutMs}ms），进程树已强制终止。请拆小命令、增大 timeout_ms（最大 {cfgM}）后重试。";
                    return new ToolResult
                    {
                        Success = false,
                        Output = timeoutDisplay,
                        Error = timeoutMsg,
                        ExitCode = -1,
                        DurationMs = sw.ElapsedMilliseconds,
                        SpilledFile = timeoutSpill,
                    };
                }
            }

            // 正常退出：等待流读取收尾（有界等待防挂起）
            byte[] stdoutBytes;
            byte[] stderrBytes;
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(15), token)
                    .ConfigureAwait(false);
                stdoutBytes = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : Array.Empty<byte>();
                stderrBytes = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : Array.Empty<byte>();
            }
            catch (Exception)
            {
                stdoutBytes = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : Array.Empty<byte>();
                stderrBytes = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : Array.Empty<byte>();
            }

            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = -1;
            }

            string stdoutText = SmartConsoleDecoder.DecodeBytes(stdoutBytes);
            string stderrText = SmartConsoleDecoder.DecodeBytes(stderrBytes);
            string combined = stdoutText;
            if (!string.IsNullOrWhiteSpace(stderrText))
            {
                combined += (combined.Length > 0 && !combined.EndsWith("\n") ? "\n" : string.Empty)
                    + "[stderr]\n" + stderrText;
            }

            if (string.IsNullOrWhiteSpace(combined))
            {
                combined = $"命令执行完成，退出码 {exitCode}，无标准输出。";
            }

            var (display, spilled) = ToolOutputGuard.ApplyGuard(
                combined, context.DataRoot, context.WorkspaceId, context.SessionId, call.Id);

            return new ToolResult
            {
                Success = exitCode == 0,
                Output = display,
                Error = exitCode == 0 ? null : $"命令退出码非零（{exitCode}）。stderr 已合并至正文，请据此修正命令后重试。",
                ExitCode = exitCode,
                DurationMs = sw.ElapsedMilliseconds,
                SpilledFile = spilled,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail(
                $"exec_command 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("命令执行已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        finally
        {
            sw.Stop();
            // 临时内联脚本执行后自动清理
            if (!string.IsNullOrWhiteSpace(tempScriptPath))
            {
                try
                {
                    if (File.Exists(tempScriptPath))
                    {
                        File.Delete(tempScriptPath);
                    }
                }
                catch
                {
                    // 忽略临时脚本清理异常
                }
            }
        }
    }

    /// <summary>
    /// need22-P0 S2：cmd 策略门（白名单首 token + 绝对路径工作区约束 + 重定向目标约束）。
    /// 通过返回 null；拒绝返回 FailCode 结果。
    /// </summary>
    internal static ToolResult? CheckCmdPolicy(
        string command, string workDir, string workspaceRoot, ToolContext context, Stopwatch sw)
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
        if (context.AllowedCommandsOverride != null && context.AllowedCommandsOverride.Count > 0)
        {
            foreach (string c in context.AllowedCommandsOverride)
            {
                if (!string.IsNullOrWhiteSpace(c))
                {
                    allowed.Add(c.Trim());
                }
            }
        }
        else
        {
            foreach (string c in DefaultAllowedCommands) allowed.Add(c);
            foreach (string c in CmdBuiltinCommands) allowed.Add(c);
        }

        // 按 & | ; 切分命令段（双引号内不切），每段首 token 都必须在白名单
        foreach (string segment in SplitSegments(command))
        {
            string first = ExtractFirstToken(segment);
            if (string.IsNullOrWhiteSpace(first))
            {
                continue;
            }

            string name = NormalizeExeName(first);
            if (!allowed.Contains(name))
            {
                return ToolResult.FailCode("COMMAND_NOT_ALLOWED",
                    $"COMMAND_NOT_ALLOWED：命令首 token '{first}' 不在白名单（允许：{string.Join("/", DefaultAllowedCommands)} + 安全 cmd 内建；可经 AllowedCommands 配置增补）。请改用白名单命令后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }
        }

        // 绝对路径参数必须落在 workDir 内（防 type/copy 读取工作区外敏感文件）
        foreach (Match m in AbsPathProbe.Matches(command))
        {
            string candidate = TakePathToken(command, m.Index);
            string abs;
            try
            {
                abs = Path.IsPathFullyQualified(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(workDir, candidate));
            }
            catch
            {
                continue;
            }

            var (resolved, _) = PathSafetyValidator.NormalizeAndResolve(abs);
            if (PathSafetyValidator.IsOutsideWorkspace(workDir, resolved))
            {
                return ToolResult.FailCode(PathSafetyValidator.Codes.OutsideWorkspace,
                    $"{PathSafetyValidator.Codes.OutsideWorkspace}：命令含工作区外绝对路径：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, resolved)}'。请改用工作区内相对路径后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }
        }

        // 重定向目标必须落在 workDir 内（防 echo ... > C:\... 绕过写保护）
        foreach (Match m in RedirectProbe.Matches(command))
        {
            string target = m.Groups[2].Value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(target) || target.StartsWith("&"))
            {
                continue; // 句柄合并（2>&1）放行
            }

            if (string.Equals(target, "nul", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "con", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string abs;
            try
            {
                abs = Path.IsPathFullyQualified(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(workDir, target));
            }
            catch
            {
                continue;
            }

            if (PathSafetyValidator.IsOutsideWorkspace(workDir, abs))
            {
                return ToolResult.FailCode("COMMAND_REDIRECT_OUTSIDE",
                    $"COMMAND_REDIRECT_OUTSIDE：重定向目标在工作区外：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, abs)}'。文件写出请改用 write_file 后重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }
        }

        return null;
    }

    internal static IEnumerable<string> SplitSegments(string command)
    {
        var segs = new List<string>();
        bool inQuote = false;
        int start = 0;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && (c == '&' || c == '|' || c == ';'))
            {
                segs.Add(command.Substring(start, i - start));
                // 跳过连续分隔符（&& / ||）
                while (i + 1 < command.Length && command[i + 1] == c)
                {
                    i++;
                }
                start = i + 1;
            }
        }
        segs.Add(command.Substring(start));
        return segs;
    }

    internal static string ExtractFirstToken(string segment)
    {
        string s = (segment ?? string.Empty).TrimStart();
        // 去 cmd 抑制回显前缀 @ 与括号块
        s = s.TrimStart('@', ' ', '\t', '(', ' ');
        if (s.Length == 0)
        {
            return string.Empty;
        }

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
        }

        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '/' && s[i] != '&' && s[i] != '|')
        {
            // 首 token 允许含 :（盘符）与 \（路径），遇空白/开关分隔即止；/ 仅当非首字符时截断
            if (s[i] == '/' && i == 0)
            {
                break;
            }
            if (s[i] == '/' && i > 0 && (s.StartsWith("for", StringComparison.OrdinalIgnoreCase) || s.StartsWith("if", StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
            i++;
        }
        // for /L ... / if /I ...：开关在首 token 后，上面已在 / 处截断
        string token = s.Substring(0, i).Trim();
        // for 的情况首 token 即 for（/L 是参数）
        int sp = token.IndexOfAny(new[] { ' ', '\t' });
        return sp >= 0 ? token.Substring(0, sp) : token;
    }

    internal static string NormalizeExeName(string firstToken)
    {
        string t = (firstToken ?? string.Empty).Trim().Trim('"');
        // 取文件名（去目录），去扩展名
        try
        {
            string file = t.Replace('/', '\\');
            int slash = file.LastIndexOf('\\');
            if (slash >= 0)
            {
                file = file.Substring(slash + 1);
            }
            int dot = file.LastIndexOf('.');
            if (dot > 0)
            {
                string ext = file.Substring(dot).ToLowerInvariant();
                if (ext is ".exe" or ".cmd" or ".bat" or ".com" or ".ps1")
                {
                    file = file.Substring(0, dot);
                }
            }
            return file.ToLowerInvariant();
        }
        catch
        {
            return t.ToLowerInvariant();
        }
    }

    private static string TakePathToken(string command, int index)
    {
        int start = index;
        int i = index;
        bool quoted = i < command.Length && command[i] == '"';
        if (quoted)
        {
            i++;
            start = i;
            while (i < command.Length && command[i] != '"') i++;
            return command.Substring(start, Math.Max(0, i - start));
        }
        while (i < command.Length && !char.IsWhiteSpace(command[i])
            && command[i] != '"' && command[i] != '&' && command[i] != '|' && command[i] != '<' && command[i] != '>')
        {
            i++;
        }
        return command.Substring(start, Math.Max(0, i - start));
    }

    private static bool LooksLikeScriptFileInvocation(string command, string workDir)
    {
        string trimmed = command.Trim();
        // 单行且引用 .py 文件 → 视为脚本调用；多行一律视为内联代码
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return false;
        }

        if (!trimmed.Contains(".py", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 首个 token 为已存在的 .py 文件
        string firstToken = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim('"') ?? string.Empty;
        if (firstToken.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string candidate = Path.IsPathFullyQualified(firstToken)
                    ? firstToken
                    : Path.Combine(workDir, firstToken);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<byte[]> ReadStreamBytesAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static async Task<string> CollectPartialAsync(Task<byte[]> stdoutTask, Task<byte[]> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略部分收集阶段的等待异常
        }

        byte[] outBytes = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : Array.Empty<byte>();
        byte[] errBytes = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : Array.Empty<byte>();
        string text = SmartConsoleDecoder.DecodeBytes(outBytes);
        string err = SmartConsoleDecoder.DecodeBytes(errBytes);
        if (!string.IsNullOrWhiteSpace(err))
        {
            text += (text.Length > 0 ? "\n" : string.Empty) + "[stderr]\n" + err;
        }

        return text;
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // 忽略强杀阶段异常（Job 关闭已完成主要回收）
            }
        }
    }

    private static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Guid.NewGuid().ToString("N");
        }

        var sb = new System.Text.StringBuilder(id.Length);
        foreach (char c in id.Trim())
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        string result = sb.ToString();
        return result.Length > 32 ? result.Substring(0, 32) : result;
    }
}
