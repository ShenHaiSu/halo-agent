using System.Diagnostics;
using System.Text.Json;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// exec_command：默认 cmd、次选 python 的命令执行引擎。
/// JobObject 进程树管控 + 异步字节流读取 + SmartDecoder + ToolOutputGuard。
/// </summary>
public sealed class ExecCommandTool : IToolHandler
{
    public const int DefaultTimeoutMs = 30000;
    public const int MaxTimeoutMs = 300000;

    public const string PythonMissingMessage =
        "Error: Python runtime is not detected on this machine. " +
        "Please fall back to 'cmd' runner or ask the user to configure Python path in Settings.";

    public string Name => "exec_command";

    public string Description =>
        "Execute a command or inline script in the workspace. " +
        "IMPORTANT FOR WIN7/WIN10/WIN11 COMPATIBILITY: Always prefer 'cmd' (default Windows Command Prompt) " +
        "for shell operations, file management, and build commands. " +
        "Secondarily prefer 'python' ('py') for complex text processing, calculation, or scripting. " +
        "Avoid PowerShell because PowerShell versions differ drastically across Windows 7 (PS 2.0) and Windows 10/11.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"command\":{\"type\":\"string\",\"description\":\"The command line to execute (when runner is 'cmd') or the Python code/script arguments to execute (when runner is 'python').\"},"
        + "\"runner\":{\"type\":\"string\",\"enum\":[\"cmd\",\"python\"],\"description\":\"Execution runtime. Defaults to 'cmd' (recommended primary choice). Use 'python' as the secondary choice for Python inline scripts (-c) or script execution.\"},"
        + "\"workdir\":{\"type\":\"string\",\"description\":\"Optional working directory relative to the current workspace root, or an absolute path. Defaults to the current workspace root.\"},"
        + "\"timeout_ms\":{\"type\":\"integer\",\"description\":\"Optional timeout in milliseconds. 0 = infinite wait (cancellable by user). If omitted defaults to server config (default 30000). Positive values are capped by server max (default 300000, 0 = no cap).\"}"
        + "},\"required\":[\"command\"]}");

    public async Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? tempScriptPath = null;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.CancellationToken);
            CancellationToken token = linkedCts.Token;

            // ---- 参数解析（need08：0=无限语义，01 §4.1）----
            string command = string.Empty;
            string runner = "cmd";
            string? workdirArg = null;
            int? parsedTimeout = null; // null=缺省/非数字
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("command", out JsonElement cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
                {
                    command = cmdEl.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("runner", out JsonElement runnerEl) && runnerEl.ValueKind == JsonValueKind.String)
                {
                    runner = (runnerEl.GetString() ?? "cmd").Trim().ToLowerInvariant();
                }

                if (root.TryGetProperty("workdir", out JsonElement workdirEl) && workdirEl.ValueKind == JsonValueKind.String)
                {
                    workdirArg = workdirEl.GetString();
                }

                if (root.TryGetProperty("timeout_ms", out JsonElement timeoutEl) && timeoutEl.ValueKind == JsonValueKind.Number
                    && timeoutEl.TryGetInt32(out int parsed))
                {
                    parsedTimeout = parsed;
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

            // need08：超时解析 0=无限（01 §4.1）；null 快照→回退旧常量，保证老单测行为不变
            int cfgD = context.ExecDefaultTimeoutMsOverride ?? DefaultTimeoutMs;
            int cfgM = context.ExecMaxTimeoutMsOverride ?? MaxTimeoutMs;
            var (timeoutMs, infinite) = Host.Desktop.Core.Models.AgentTimeoutResolver.Resolve(parsedTimeout, cfgD, cfgM);

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            string workDir = PathSafetyValidator.ResolveWorkspacePath(workspaceRoot, workdirArg);
            if (!Directory.Exists(workDir))
            {
                return ToolResult.Fail(
                    $"工作目录不存在：'{workDir}'。请使用 read_file/list_dir 确认路径后重试（workdir 为空默认使用工作区根）。",
                    durationMs: sw.ElapsedMilliseconds);
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
                CommandRunnerProbe.ProbeResult probe = context.PythonProbeOverride != null
                    ? context.PythonProbeOverride()
                    : CommandRunnerProbe.ProbePython(configuredPath: context.ConfiguredPythonPath);

                if (!probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
                {
                    return ToolResult.Fail(PythonMissingMessage, durationMs: sw.ElapsedMilliseconds);
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

            if (infinite)
            {
                Infrastructure.Logging.HaloLogger.Tool.Info($"exec_command infinite wait (call {call.Id})，仅用户取消可终止");
                // 不建 timeoutCts，直接等用户取消；取消时显式杀树后向外抛（外层统一转“已被取消”）
                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    job.Dispose();
                    TryKill(process);
                    throw;
                }
            }
            else
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
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
