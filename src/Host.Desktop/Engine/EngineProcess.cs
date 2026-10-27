using System.Diagnostics;
using System.Text;

namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 引擎子进程完整生命周期：启动 / stdout+stderr 双泵 / 五步优雅关闭。
/// stdout/stderr 必须各起独立 Task 泵，否则管道满会阻塞引擎（心跳停→误判卡死→重启死循环）。
/// </summary>
public sealed class EngineProcess : IAsyncDisposable
{
    private readonly EngineProcessOptions _opts;
    private Process? _proc;
    private WindowsJob? _job;

    public EngineProcess(EngineProcessOptions opts) => _opts = opts;

    public bool IsRunning => _proc is { HasExited: false };
    public int? Pid => _proc?.Id;
    public event EventHandler<int>? ExitedUnexpectedly;

    public void Start()
    {
        if (IsRunning) throw new InvalidOperationException("引擎已在运行");
        if (!File.Exists(_opts.EngineExePath))
            throw new FileNotFoundException("找不到引擎程序 HaloAgent.Engine.exe", _opts.EngineExePath);

        var psi = new ProcessStartInfo
        {
            FileName = _opts.EngineExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = Path.GetDirectoryName(_opts.EngineExePath)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--pipe-name");
        psi.ArgumentList.Add(_opts.PipeName);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(_opts.DataDir);
        psi.ArgumentList.Add("--cwd");
        psi.ArgumentList.Add(_opts.Cwd);
        psi.ArgumentList.Add("--log-dir");
        psi.ArgumentList.Add(_opts.LogDir);
        if (_opts.Headless) psi.ArgumentList.Add("--headless");
        if (!string.IsNullOrWhiteSpace(_opts.ExtraArgs))
            foreach (var a in _opts.ExtraArgs!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                psi.ArgumentList.Add(a);

        psi.Environment["HALO_LAUNCHED_BY"] = "desktop";
        psi.Environment["HALO_HOST_PID"] = Environment.ProcessId.ToString();

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动引擎进程");
        AppLog.Info($"引擎已启动 PID={_proc.Id} 管道={_opts.PipeName}");

        try
        {
            _job = WindowsJob.CreateKillOnClose();
            _job.Assign(_proc);
        }
        catch (Exception ex)
        {
            // Job 失败不致命（精简版/域控可能受限），OrphanCleaner 兜底。
            AppLog.Warn($"无法把引擎加入 Job Object（宿主崩溃时可能残留引擎进程）：{ex.Message}");
        }

        _ = PumpAsync(_proc.StandardOutput, "engine-out");
        _ = PumpAsync(_proc.StandardError, "engine-err");

        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) =>
        {
            var code = -1;
            try { code = _proc?.ExitCode ?? -1; } catch { /* 退出码不可读时记 -1 */ }
            AppLog.Info($"引擎进程退出，退出码 {code}");
            if (code != 0)
                ExitedUnexpectedly?.Invoke(this, code);
        };
    }

    private static async Task PumpAsync(StreamReader reader, string tag)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                AppLog.Info($"[{tag}] {line}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{tag} 读取中断：{ex.Message}");
        }
    }

    /// <summary>五步优雅关闭：等优雅退出(3s) → CloseMainWindow(2s) → 强杀进程树。</summary>
    public async Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        if (_proc is null) return;
        if (_proc.HasExited) { Cleanup(); return; }
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(3);
        try
        {
            AppLog.Info("等待引擎优雅退出…");
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                AppLog.Info("引擎已优雅退出");
                Cleanup();
                return;
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn($"引擎在 {timeout.TotalSeconds}s 内未退出，继续下一步");
            }

            try { _proc.CloseMainWindow(); } catch { /* 无窗口进程时无害 */ }
            using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    await _proc.WaitForExitAsync(cts2.Token).ConfigureAwait(false);
                    AppLog.Info("引擎在 CloseMainWindow 后退出");
                    Cleanup();
                    return;
                }
                catch (OperationCanceledException) { /* 继续强杀 */ }
            }

            AppLog.Warn("强制结束引擎进程树");
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("关闭引擎时出错", ex);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        try { _job?.Dispose(); } catch { /* 释放失败可忽略 */ }
        _job = null;
        try { _proc?.Dispose(); } catch { /* 释放失败可忽略 */ }
        _proc = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
