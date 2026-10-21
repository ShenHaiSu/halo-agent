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
[HaloAgent.Tools.Tool("exec_command")]
public sealed class ExecCommandTool : IToolHandler
{
    public string Name => "exec_command";

    public string Description =>
        "Execute a command or inline script in the workspace. "
        + "IMPORTANT FOR WIN7/WIN10/WIN11 COMPATIBILITY: Always prefer 'cmd' (default Windows Command Prompt) "
        + "for shell operations, file management, and build commands. "
        + "Secondarily prefer 'python' ('py') for complex text processing, calculation, or scripting. "
        + "Avoid PowerShell because PowerShell versions differ drastically across Windows 7 (PS 2.0) and Windows 10/11. "
        + "Only whitelisted executables may run (git/node/pnpm/npm/python/pip/dotnet + safe cmd builtins); others are rejected. "
        + "Output keeps the TAIL (last lines) on truncation; full text beyond the budget is spilled to a log file. "
        + "Large outputs are truncated to head+tail within a fixed budget; the full text is spilled to a log file path in the result. Use read_file(offset,limit) or grep_search to page, never re-run a broad command to dump everything.";

    public ToolDefinition GetDefinition()
    {
        string desc = Description;
        const string NoPyNote = " (注意：当前环境未检测到 Python，runner:'python' 不可用，请使用 'cmd')";
        string runnerNote = PythonAvailabilitySnapshot.Current.Found ? string.Empty : NoPyNote;
        if (runnerNote.Length > 0)
        {
            desc += NoPyNote;
        }
        return new ToolDefinition(
            Name,
            desc,
            "{\"type\":\"object\",\"properties\":{"
                + "\"command\":{\"type\":\"string\",\"description\":\"Full command line (runner cmd) or Python code/script args (runner python). Runs in workspace root; stdout+stderr merged. Max 8192 chars; cmd runner only allows whitelisted executables.\"},"
                + "\"runner\":{\"type\":\"string\",\"enum\":[\"cmd\",\"python\"],\"default\":\"cmd\",\"description\":\"cmd first. python for text processing. No PowerShell."
                + runnerNote
                + "\"},"
                + "\"workdir\":{\"type\":\"string\",\"description\":\"Optional dir relative to workspace root. Defaults to workspace root. Must stay inside the workspace; missing dir fails fast.\"},"
                + "\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"default\":60000,\"maximum\":300000,\"description\":\"Optional timeout in ms. Must be >=1. Defaults 60000. Capped 300000.\"}"
                + "},\"required\":[\"command\"],\"additionalProperties\":false}"
        );
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        string? tempScriptPath = null;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.CancellationToken
            );
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
                    durationMs: sw.ElapsedMilliseconds
                );
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                return ToolResult.Fail(
                    "缺少必需参数 'command'。cmd 示例：{\"command\": \"dir /b\"}；python 示例：{\"command\": \"print('hi')\", \"runner\": \"python\"}。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            if (runner != "cmd" && runner != "python")
            {
                return ToolResult.Fail(
                    $"不支持的 runner '{runner}'，仅支持 'cmd'（默认首选）与 'python'（次选）。请修正 runner 后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            // need22-P0 S2：长度上限 8K
            if (command.Length > ToolPolicy.MaxCommandChars)
            {
                return ToolResult.FailCode(
                    "COMMAND_TOO_LONG",
                    $"COMMAND_TOO_LONG：command 长 {command.Length} 字符，超过上限 {ToolPolicy.MaxCommandChars}。请拆小命令后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;

            // need22-P0 S2：workdir 强制 ValidateWrite（越界即 PATH_OUTSIDE_WORKSPACE）
            string workDir;
            {
                var (ok, err, full, _) = PathSafetyValidator.ValidateWrite(
                    workspaceRoot,
                    workdirArg
                );
                if (!ok)
                {
                    return ToolResult.FailCode(
                        PathSafetyValidator.Codes.OutsideWorkspace,
                        err!,
                        durationMs: sw.ElapsedMilliseconds
                    );
                }
                workDir = full;
            }
            if (!Directory.Exists(workDir))
            {
                return ToolResult.Fail(
                    $"工作目录不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, workDir)}'。请使用 read_file/list_dir 确认路径后重试（workdir 为空默认使用工作区根）。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            // need22-P0 S2：cmd runner 白名单 + 路径/重定向收敛（python runner 为代码形态，跳过 shell 校验）
            if (runner == "cmd")
            {
                ToolResult? gate = Process.ExecProcessHelper.CheckCmdPolicy(
                    command,
                    workDir,
                    workspaceRoot,
                    context,
                    sw
                );
                if (gate != null)
                {
                    return gate;
                }
            }

            // need22-P0 S2：超时默认 60s、上限 300s；timeout_ms<=0 非法（删除 0=无限语义）
            if (parsedTimeout.HasValue && parsedTimeout.Value <= 0)
            {
                return ToolResult.FailCode(
                    "INVALID_TIMEOUT",
                    $"INVALID_TIMEOUT：timeout_ms 非法（{parsedTimeout.Value}），必须 ≥1（默认 {ToolPolicy.DefaultTimeoutMs}，上限 {ToolPolicy.MaxTimeoutMs}）。不允许无限等待。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }
            int cfgD =
                context.ExecDefaultTimeoutMsOverride is int d && d > 0
                    ? d
                    : ToolPolicy.DefaultTimeoutMs;
            int cfgM =
                context.ExecMaxTimeoutMsOverride is int m && m > 0 ? m : ToolPolicy.MaxTimeoutMs;
            var (timeoutMs, _) = Host.Desktop.Core.Models.AgentTimeoutResolver.Resolve(
                parsedTimeout,
                cfgD,
                cfgM
            );
            if (timeoutMs <= 0)
            {
                timeoutMs = ToolPolicy.DefaultTimeoutMs;
            }

            // need22-P0 S2：审计日志（命令全文，脱敏后；长度截断防爆日志）
            try
            {
                var (scrubbed, _) = SecretScrubber.Scrub(command);
                string preview =
                    scrubbed.Length > 500 ? scrubbed.Substring(0, 500) + "..." : scrubbed;
                Infrastructure.Logging.HaloLogger.Tool.Info(
                    $"exec_command audit (call {call.Id}) runner={runner} timeout={timeoutMs}ms workdir={PathSafetyValidator.ToDisplayPath(workspaceRoot, workDir)} cmd={preview}"
                );
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
                        durationMs: sw.ElapsedMilliseconds
                    );
                }

                psi = CommandRunnerProbe.BuildCmdStartInfo(command, workDir);
            }
            else
            {
                var (probe, probeFail) = PythonFallbackPolicy.ResolveProbe(context, runner, sw);
                if (probeFail != null)
                {
                    return probeFail;
                }

                string pythonArgs;
                if (Process.ExecProcessHelper.LooksLikeScriptFileInvocation(command, workDir))
                {
                    pythonArgs = CommandRunnerProbe.BuildPythonArguments(probe!, command);
                }
                else
                {
                    // 多行/内联代码 → 落盘为 UTF-8 临时脚本后 -u 执行，规避引号转义地狱
                    Directory.CreateDirectory(context.SessionTempDir);
                    tempScriptPath = Path.Combine(
                        context.SessionTempDir,
                        $"exec_{Process.ExecProcessHelper.SanitizeId(call.Id)}.py"
                    );
                    await File.WriteAllTextAsync(
                            tempScriptPath,
                            command,
                            new System.Text.UTF8Encoding(false),
                            token
                        )
                        .ConfigureAwait(false);
                    pythonArgs = CommandRunnerProbe.BuildPythonArguments(
                        probe!,
                        "-u \"" + tempScriptPath + "\""
                    );
                }

                psi = CommandRunnerProbe.BuildPythonStartInfo(probe!.ExePath, pythonArgs, workDir);
            }

            // ---- 启动进程（JobObject 护航）----
            using var job = JobObjectManager.Create();
            using var process = new System.Diagnostics.Process
            {
                StartInfo = psi,
                EnableRaisingEvents = false
            };

            bool started;
            try
            {
                started = process.Start();
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    $"进程启动失败：{ex.Message}。请检查命令与工作目录后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            if (!started)
            {
                return ToolResult.Fail(
                    "进程启动失败（Process.Start 返回 false）。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            job.Assign(process);

            // 异步原始字节读取（BaseStream 直读，避免编码死锁）
            Task<byte[]> stdoutTask = Process.ExecProcessHelper.ReadStreamBytesAsync(
                process.StandardOutput.BaseStream,
                token
            );
            Task<byte[]> stderrTask = Process.ExecProcessHelper.ReadStreamBytesAsync(
                process.StandardError.BaseStream,
                token
            );

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
                    Process.ExecProcessHelper.TryKill(process);
                    string partial = await Process
                        .ExecProcessHelper.CollectPartialAsync(stdoutTask, stderrTask)
                        .ConfigureAwait(false);
                    var (timeoutDisplay, timeoutSpill) = ToolOutputGuard.ApplyGuard(
                        partial,
                        context.DataRoot,
                        context.WorkspaceId,
                        context.SessionId,
                        call.Id
                    );
                    string timeoutMsg =
                        $"命令执行超时（{timeoutMs}ms），进程树已强制终止。请拆小命令、增大 timeout_ms（最大 {cfgM}）后重试。";
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
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(TimeSpan.FromSeconds(15), token)
                    .ConfigureAwait(false);
                stdoutBytes = stdoutTask.IsCompletedSuccessfully
                    ? stdoutTask.Result
                    : Array.Empty<byte>();
                stderrBytes = stderrTask.IsCompletedSuccessfully
                    ? stderrTask.Result
                    : Array.Empty<byte>();
            }
            catch (Exception)
            {
                stdoutBytes = stdoutTask.IsCompletedSuccessfully
                    ? stdoutTask.Result
                    : Array.Empty<byte>();
                stderrBytes = stderrTask.IsCompletedSuccessfully
                    ? stderrTask.Result
                    : Array.Empty<byte>();
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
                combined +=
                    (combined.Length > 0 && !combined.EndsWith("\n") ? "\n" : string.Empty)
                    + "[stderr]\n"
                    + stderrText;
            }

            if (string.IsNullOrWhiteSpace(combined))
            {
                combined = $"命令执行完成，退出码 {exitCode}，无标准输出。";
            }

            var (display, spilled) = ToolOutputGuard.ApplyGuard(
                combined,
                context.DataRoot,
                context.WorkspaceId,
                context.SessionId,
                call.Id
            );

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
                durationMs: sw.ElapsedMilliseconds
            );
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
}
