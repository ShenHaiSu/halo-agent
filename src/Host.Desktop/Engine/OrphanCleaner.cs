using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 启动时清理上次残留引擎。绝不“杀掉所有同名进程”——必须 PID + 启动时间 + 进程名三重校验（防 PID 复用）。
/// </summary>
public static class OrphanCleaner
{
    private static string LockDir =>
        Path.Combine(Path.GetTempPath(), "haloagent-locks");

    public static void WriteLock(int pid, string pipeName)
    {
        try
        {
            Directory.CreateDirectory(LockDir);
            var info = new
            {
                pid,
                pipe = pipeName,
                startedAt = DateTime.UtcNow.ToString("O"),
                exe = Environment.ProcessPath,
            };
            File.WriteAllText(Path.Combine(LockDir, $"{pid}.lock"), JsonSerializer.Serialize(info));
        }
        catch { /* 清理功能失败不影响主流程 */ }
    }

    public static void RemoveLock(int pid)
    {
        try { File.Delete(Path.Combine(LockDir, $"{pid}.lock")); } catch { /* 删除失败可忽略 */ }
    }

    public static int Cleanup()
    {
        if (!Directory.Exists(LockDir)) return 0;
        int cleaned = 0;
        foreach (var file in Directory.EnumerateFiles(LockDir, "*.lock"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var pid = doc.RootElement.GetProperty("pid").GetInt32();
                var startedAt = DateTime.Parse(
                    doc.RootElement.GetProperty("startedAt").GetString()!,
                    null, DateTimeStyles.RoundtripKind);

                Process? p = null;
                try { p = Process.GetProcessById(pid); }
                catch (ArgumentException) { /* 进程不存在 */ }

                if (p is null)
                {
                    AppLog.Info($"清理僵尸 lock：PID {pid} 已不存在");
                    File.Delete(file);
                    cleaned++;
                }
                else
                {
                    using (p)
                    {
                        var sameStart = Math.Abs((p.StartTime.ToUniversalTime() - startedAt).TotalSeconds) < 5;
                        var name = p.ProcessName;
                        if (sameStart && name.Contains("HaloAgent", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLog.Warn($"发现上次残留的引擎进程 PID {pid}（{name}），正在结束");
                            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { /* 强杀失败则放弃 */ }
                            cleaned++;
                        }
                        else
                        {
                            AppLog.Info($"lock 中的 PID {pid} 已被其他程序复用，忽略并删除 lock");
                        }
                    }
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"清理 lock 失败 {file}：{ex.Message}");
                try { File.Delete(file); } catch { /* 删除失败可忽略 */ }
            }
        }
        return cleaned;
    }
}
