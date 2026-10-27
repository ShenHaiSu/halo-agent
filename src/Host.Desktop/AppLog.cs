namespace HaloAgent.Desktop;

/// <summary>
/// 宿主侧极简日志。写到 %LOCALAPPDATA%\HaloAgent\logs\desktop-yyyyMMdd.log。
/// 为什么不用 ILogger：
///   - 宿主只需要"记一行 + 记堆栈"，引入 DI 容器是过度设计；
///   - 存在"日志系统本身初始化失败"的极端情况，这里必须零依赖、不会抛异常。
/// 任何写日志的失败都必须被吞掉——日志不能成为崩溃原因。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string _dir = "";
    private static string _file = "";

    public static string CurrentFile => _file;

    public static void Initialize()
    {
        try
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Program.ProductName, "logs");
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, $"desktop-{DateTime.Now:yyyyMMdd}.log");
            CleanupOldFiles();
            Info("=== 会话开始 ===");
            Info($"宿主版本={BuildInfo.Version} OS={Environment.OSVersion} 64位进程={Environment.Is64BitProcess}");
        }
        catch
        {
            // 极端情况：连日志目录都建不了。不阻断启动。
            _dir = Path.GetTempPath();
            _file = Path.Combine(_dir, "haloagent-desktop.log");
        }
    }

    private static void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_dir, "desktop-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    public static void Info(string msg) => Write("INF", msg, null);
    public static void Warn(string msg) => Write("WRN", msg, null);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                using var w = new StreamWriter(
                    new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.UTF8);
                w.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
                if (ex is not null) w.WriteLine(ex.ToString());
            }
        }
        catch { }
    }
}
