using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Util;

namespace HaloAgent.Engine.Core.Tools.Builtin;

/// <summary>
/// run_shell_command：执行终端命令。
///
/// ============ 五个机制同时作用 ============
///
/// ① 静默超时（固定 90s）：连续无输出 → 判卡死，杀掉。
///    覆盖场景：命令等待输入、网络挂起、死锁。
///
/// ② 总预算（模型传 idle_ms，可选）：AI 预估的完成时长。
///    不传 = 无预算约束（只靠静默 + 总保险）。
///
/// ③ 超预算宽限（预算的 20%，最多 60s）：预算耗尽后仍给一点缓冲，
///    避免“差 3 秒”被整轮重跑。
///
/// ④ 总墙钟保险（固定 30min）：有输出但失控（刷屏死循环）也必杀。
///
/// ⑤ 实时输出（200ms 节流）+ 静默预警（80% 黄 / 90% 红）。
///
/// ============ 被杀时回给模型的“死前遗言” ============
/// 不是一句“超时了”，而是：
///   - 终止原因（哪种机制触发的）
///   - 运行时长 / 预算 / 宽限
///   - 最后 1500 字符的 stdout + stderr
/// 这样模型能自己决定：加大预算重试、拆小步骤、还是换更轻的命令。
/// </summary>
public sealed class RunShellCommandTool : ITool
{
    // ---------- 固定参数 ----------
    private static readonly TimeSpan SilentCap = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WallClockCap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TickerPeriod = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RedAlertThreshold = TimeSpan.FromSeconds(10);

    private const int GracePercent = 20;
    private static readonly TimeSpan GraceCap = TimeSpan.FromMinutes(1);
    private const int OutputBufferCap = 256 * 1024;
    private const int ContextMaxChars = 20_000;
    private const int DeathTailChars = 1500;

    public const long MaxBudgetMs = 30L * 60 * 1000;

    public string Name => "run_shell_command";

    public string Description =>
        "执行一条终端命令(shell 命令,支持管道/重定向/&& 等)。" +
        "高危命令(递归删根/家目录、dd 或 mkfs 写块设备、git push --force 等)会被启发式判定:" +
        "命中会要求用户确认,无头模式直接拒绝。判定看的是解析出的目标路径," +
        "所以请写明确路径,不要用变量或通配符(会因无法判定而要求确认)";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse(@"{
      ""type"": ""object"",
      ""properties"": {
        ""command"": {
          ""type"": ""string"",
          ""description"": ""要执行的完整命令,如 'ls -la'""
        },
        ""cwd"": {
          ""type"": ""string"",
          ""description"": ""执行目录,缺省为当前工作目录""
        },
        ""idle_ms"": {
          ""type"": ""integer"",
          ""description"": ""AI 预估的完成时长(毫秒):命令跑超它(含预算 20%、最多 60s 的宽限)仍未结束就会被终止,终止时会反馈运行进展与死前输出,由你决定:调大 idle_ms 重试、拆小步骤或换更轻的命令。宁大勿小——超时被杀整轮白跑、重试更慢;命令连续无输出超过 90s 会被判卡死(内部规则,无需理会)""
        }
      },
      ""required"": [""command""]
    }").RootElement.Clone();

    public PermissionChecker.Operation Operation => PermissionChecker.Operation.RunCommand;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.GetStringOrNull("command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failure(
                "command 不能为空:请传入要执行的完整命令字符串,如 {\"command\": \"ls -la\"}");

        var cwdRaw = args.GetStringOrNull("cwd");
        var dir = cwdRaw is null ? ctx.Cwd : ToolPaths.Resolve(ctx.Cwd, cwdRaw);
        if (!Directory.Exists(dir))
            return ToolResult.Failure($"执行目录不存在: {dir}");

        var idleMs = args.GetLongOrNull("idle_ms");
        TimeSpan? budget = idleMs is null
            ? null
            : TimeSpan.FromMilliseconds(Math.Min(idleMs.Value, MaxBudgetMs));

        return await RunAsync(command, dir, SilentCap, budget, ctx, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  主执行循环
    // ============================================================

    private async Task<ToolResult> RunAsync(
        string command, string workDir,
        TimeSpan silentCap, TimeSpan? budget,
        ToolContext ctx, CancellationToken ct)
    {
        // ctx.Ct 与调用方 ct 任一取消都视为取消
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.Ct);
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();

        var psi = BuildProcessStartInfo(command, workDir);

        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                return ToolResult.Failure("启动命令失败: Process.Start 返回 false");
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"启动命令失败: {ex.Message}");
        }

        using (process)
        {
            var killReasons = new ConcurrentQueue<string>();
            var stdout = process.StandardOutput.BaseStream;
            var stderr = process.StandardError.BaseStream;

            var outBuf = new RollingBuffer(OutputBufferCap);
            var errBuf = new RollingBuffer(OutputBufferCap);
            var pendingDelta = new StringBuilder();

            var outChunk = new byte[8192];
            var errChunk = new byte[8192];

            var startedAt = Stopwatch.StartNew();
            var lastOutputAt = Stopwatch.StartNew();
            int currentAlert = 0;

            using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ticker = Task.Run(() => TickerLoopAsync(), tickerCts.Token);

            try
            {
                // 读循环
                var readTasks = new List<Task>
                {
                    ReadPipeAsync(stdout, outBuf, outChunk, token),
                    ReadPipeAsync(stderr, errBuf, errChunk, token),
                };

                // 等：命令自然结束 / 被杀 / 中断
                var exitTask = WaitForExitWithKillAsync();
                var completed = await Task.WhenAny(
                    exitTask,
                    Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);

                if (completed != exitTask)
                {
                    // 用户中断
                    TryKill(process);
                    await Task.WhenAny(exitTask, Task.Delay(3000)).ConfigureAwait(false);
                    return ToolResult.Failure(
                        "命令已中断(用户按 Esc),进程已终止" + BuildDeathTail(outBuf, errBuf));
                }

                string? killReason;
                try { killReason = await exitTask.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    return ToolResult.Failure(
                        "命令已中断(用户按 Esc),进程已终止" + BuildDeathTail(outBuf, errBuf));
                }
                if (killReason is not null) return ToolResult.Failure(killReason);

                await Task.WhenAll(readTasks).ConfigureAwait(false);

                // 正常结束：flush 残余实时输出
                FlushPending();

                return BuildSuccessResult(process, outBuf, errBuf);
            }
            finally
            {
                tickerCts.Cancel();
                try { await Task.WhenAny(ticker, Task.Delay(1000)).ConfigureAwait(false); } catch { }
            }

            // ---------------- 局部函数 ----------------

            async Task ReadPipeAsync(Stream stream, RollingBuffer buf, byte[] chunk,
                CancellationToken token)
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
                        if (n == 0) break;

                        buf.Append(chunk, n);
                        lock (pendingDelta) pendingDelta.Append(Encoding.UTF8.GetString(chunk, 0, n));
                        lastOutputAt.Restart();

                        if (currentAlert != 0)
                        {
                            currentAlert = 0;
                            ctx.Host.PublishSilentAlert(ToolCallId(ctx), 0);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* 管道关闭：正常 */ }
            }

            async Task TickerLoopAsync()
            {
                using var timer = new PeriodicTimer(TickerPeriod);
                try
                {
                    while (await timer.WaitForNextTickAsync(tickerCts.Token).ConfigureAwait(false))
                    {
                        // ⑤ 实时输出（200ms 一次）
                        FlushPending();

                        var silent = lastOutputAt.Elapsed;
                        var total = startedAt.Elapsed;

                        // ④ 总墙钟保险
                        if (total >= WallClockCap)
                        {
                            TryKill(process);
                            killReasons.Enqueue(
                                $"命令运行超过 {WallClockCap.TotalMinutes:0} 分钟总上限,已终止。"
                                + BuildDeathTail(outBuf, errBuf));
                            return;
                        }

                        // ① 静默超时
                        if (silent >= silentCap)
                        {
                            TryKill(process);
                            killReasons.Enqueue(
                                $"命令已连续 {silent.TotalSeconds:0}s 没有新输出,判定卡死已终止。"
                                + BuildDeathTail(outBuf, errBuf));
                            return;
                        }

                        // ② + ③ 预算与宽限
                        if (budget is { } b)
                        {
                            var grace = OverrunGrace(b);
                            if (total > b + grace)
                            {
                                TryKill(process);
                                killReasons.Enqueue(
                                    $"命令运行 {total.TotalSeconds:0}s 已超过 AI 预估预算({FormatDuration(b)}," +
                                    $"预算耗尽后另有 {grace.TotalSeconds:0}s 宽限)仍未结束,已终止。" +
                                    BuildDeathTail(outBuf, errBuf) +
                                    "\n若该任务确实需要更久,请调大 idle_ms 后重试,或拆小步骤、换更轻的命令。");
                                return;
                            }
                        }

                        // 预警分级
                        int level = ComputeAlertLevel(silentCap, silent);
                        if (level != currentAlert)
                        {
                            currentAlert = level;
                            ctx.Host.PublishSilentAlert(ToolCallId(ctx), level);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }

            async Task<string?> WaitForExitWithKillAsync()
            {
                await Task.Run(() => process.WaitForExit(), token).ConfigureAwait(false);

                return killReasons.TryDequeue(out var reason) ? reason : null;
            }

            void FlushPending()
            {
                string? chunk = null;
                lock (pendingDelta)
                {
                    if (pendingDelta.Length > 0)
                    {
                        chunk = pendingDelta.ToString();
                        pendingDelta.Clear();
                    }
                }
                if (!string.IsNullOrEmpty(chunk))
                    ctx.Host.PublishToolOutput(ToolCallId(ctx), chunk);
            }
        }
    }

    // ============================================================
    //  辅助
    // ============================================================

    private static ProcessStartInfo BuildProcessStartInfo(string command, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,   // 关键：不继承 stdin，防抢终端输入
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            // Windows：优先 PowerShell（表达力强），回退 cmd
            // 但为了与用户“在 cmd 里跑”的直觉一致，默认用 cmd /C
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.Arguments = "/D /S /C \"" + command + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        return psi;
    }

    private static string ToolCallId(ToolContext ctx) =>
        // 由 Session 在事件里带 toolCallId；这里拿不到时用空串（前端按“当前执行中的那个”处理）
        ctx.ToolCallId ?? "";

    private static TimeSpan OverrunGrace(TimeSpan budget)
    {
        var grace = TimeSpan.FromMilliseconds(budget.TotalMilliseconds * GracePercent / 100);
        return grace > GraceCap ? GraceCap : grace;
    }

    private static int ComputeAlertLevel(TimeSpan silentCap, TimeSpan silent)
    {
        // 距上限不足 10s → 红
        if (silent + RedAlertThreshold >= silentCap) return 2;

        // 静默超过 80% → 黄
        if (silent.TotalMilliseconds * 5 >= silentCap.TotalMilliseconds * 4) return 1;

        return 0;
    }

    private static string FormatDuration(TimeSpan d)
    {
        var secs = (int)d.TotalSeconds;
        if (secs >= 60 && secs % 60 == 0) return $"{secs / 60}min";
        return $"{secs}s";
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { }
    }

    private static string BuildDeathTail(RollingBuffer outBuf, RollingBuffer errBuf)
    {
        var outTail = outBuf.TailText(DeathTailChars);
        var errTail = errBuf.TailText(DeathTailChars);

        if (string.IsNullOrWhiteSpace(outTail) && string.IsNullOrWhiteSpace(errTail))
            return "\n命令没有任何输出。";

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(outTail))
            sb.Append("\n命令最后输出:\n").Append(outTail.Trim());
        if (!string.IsNullOrWhiteSpace(errTail))
            sb.Append("\n命令最后 stderr:\n").Append(errTail.Trim());
        return sb.ToString();
    }

    private static ToolResult BuildSuccessResult(Process p, RollingBuffer outBuf, RollingBuffer errBuf)
    {
        var outText = outBuf.ToText();
        var errText = errBuf.ToText();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(outText)) sb.Append(outText.TrimEnd());
        if (!string.IsNullOrWhiteSpace(errText))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("[stderr] ").Append(errText.TrimEnd());
        }

        // 长输出保留头尾、中间折叠（上下文窗口保护）
        string body = sb.Length == 0 ? "" : TextUtil.TruncateMiddle(sb.ToString(), ContextMaxChars);
        var result = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(body)) result.Append(body).Append('\n');

        int? code = null;
        try { code = p.ExitCode; } catch { }
        result.Append(code is null ? "(命令被信号终止)\n" : $"(命令退出码 {code})\n");

        return ToolResult.Success(result.ToString());
    }

    // ============================================================
    //  滚动缓冲
    // ============================================================

    /// <summary>
    /// 有界字节缓冲：超上限丢头部一半。
    /// 为什么丢头部：展示与“死前遗言”都只关心最新输出。
    /// </summary>
    private sealed class RollingBuffer
    {
        private readonly List<byte> _bytes = new();
        private readonly int _cap;

        public RollingBuffer(int cap) => _cap = cap;

        public void Append(byte[] chunk, int count)
        {
            _bytes.AddRange(new ArraySegment<byte>(chunk, 0, count));
            if (_bytes.Count > _cap)
            {
                int drop = _bytes.Count - _cap / 2;
                _bytes.RemoveRange(0, drop);
            }
        }

        public string ToText() => Encoding.UTF8.GetString(_bytes.ToArray());

        public string TailText(int maxChars)
        {
            var text = ToText().TrimEnd();
            return TextUtil.TakeLast(text, maxChars);
        }
    }
}
