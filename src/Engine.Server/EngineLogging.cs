using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Server;

/// <summary>
/// 极简文件日志。不引第三方日志库：
///   - Serilog/NLog 会拖入一串依赖，且在 Win7 上有额外风险；
///   - 我们要的功能只有"分级 + 按天滚动 + 崩溃堆栈"。
/// 文件位置：{DataDir}\logs\{prefix}-{yyyyMMdd}.log
/// 保留策略：启动时清掉 7 天前的（不占用户磁盘）。
/// </summary>
public static class EngineLogging
{
    public static ILoggerFactory Create(string level, string dataDir, string prefix, bool writeToConsole = true)
    {
        var min = ParseLevel(level);
        var dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dir);
        Cleanup(dir, prefix);

        return LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(min);
            b.AddProvider(new SimpleFileLoggerProvider(dir, prefix, min));
            // ⚠️ stdio 模式下 stdout 归帧独占，日志只能写文件（与 stderr）。
            // 调试时同时输出到控制台会污染 stdout，所以只在非 stdio 模式启用。
            if (writeToConsole)
            {
                b.AddSimpleConsole(c =>
                {
                    c.SingleLine = true;
                    c.TimestampFormat = "HH:mm:ss.fff ";
                });
            }
        });
    }

    private static void Cleanup(string dir, string prefix)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(dir, $"{prefix}-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    public static LogLevel ParseLevel(string s) => s.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    public static string DescribeEnvironment(EngineStartupOptions o)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== HaloAgent 引擎环境诊断 ===");
        sb.AppendLine($"引擎版本      : {Abstractions.HaloVersion.Engine}");
        sb.AppendLine($"协议版本      : {Abstractions.HaloVersion.ProtocolVersion}");
        sb.AppendLine($"进程 ID       : {Environment.ProcessId}");
        sb.AppendLine($"操作系统      : {Environment.OSVersion}");
        sb.AppendLine($"是 Win7       : {IsWin7()}");
        sb.AppendLine($"64 位系统     : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64 位进程     : {Environment.Is64BitProcess}");
        sb.AppendLine($".NET 运行时   : {Environment.Version}");
        sb.AppendLine($"数据目录      : {o.DataDir}");
        sb.AppendLine($"日志目录      : {Path.Combine(o.DataDir, "logs")}");
        sb.AppendLine($"运行模式      : {o.Mode}");
        sb.AppendLine($"工作目录      : {Environment.CurrentDirectory}");
        sb.AppendLine($"处理器数      : {Environment.ProcessorCount}");
        sb.AppendLine($"系统内存(MB)  : {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024}");
        return sb.ToString();
    }

    /// <summary>Win7 = NT 6.1。注意：没有 app.manifest 的 supportedOS 声明时，这里会谎报 6.2。</summary>
    public static bool IsWin7()
    {
        var v = Environment.OSVersion.Version;
        return v.Major == 6 && v.Minor == 1;
    }
}

/// <summary>按天滚动的极简文件日志 provider。</summary>
public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly string _prefix;
    private readonly LogLevel _min;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _currentDay;

    public SimpleFileLoggerProvider(string dir, string prefix, LogLevel min)
    {
        _dir = dir; _prefix = prefix; _min = min;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _min) return;
        lock (_lock)
        {
            var today = DateTime.Today;
            if (_writer is null || today != _currentDay)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _currentDay = today;
                var path = Path.Combine(_dir, $"{_prefix}-{today:yyyyMMdd}.log");
                _writer = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.UTF8)
                { AutoFlush = true };
            }
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(level)}] {category}: {message}");
            if (ex is not null)
            {
                _writer.WriteLine(ex.ToString());
            }
        }
    }

    private static string Level(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF",
    };

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly SimpleFileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(SimpleFileLoggerProvider p, string cat) { _p = p; _cat = cat; }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _p._min;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _p.Write(logLevel, _cat, formatter(state, exception), exception);
        }
    }
}
