using System.Text.Json;

namespace HaloAgent.Desktop.Diagnostics;

/// <summary>用“运行时写标记、正常退出删除”检测非法退出。比事件日志可靠。</summary>
public static class CrashMarker
{
    private static string MarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "HaloAgent", "running.marker");

    public static void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                startedAt = DateTime.UtcNow,
                version = typeof(CrashMarker).Assembly.GetName().Version?.ToString(),
            }));
        }
        catch { /* 标记写失败不影响主流程 */ }
    }

    public static CrashMarkerInfo? CheckAndRead()
    {
        if (!File.Exists(MarkerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<CrashMarkerInfo>(File.ReadAllText(MarkerPath));
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { File.Delete(MarkerPath); } catch { /* 忽略：保存失败不影响退出 */ }
    }
}

public sealed class CrashMarkerInfo
{
    public int Pid { get; set; }
    public DateTime StartedAt { get; set; }
    public string? Version { get; set; }
}
