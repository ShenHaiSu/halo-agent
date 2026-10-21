using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Logging;

public enum HaloLogChannel
{
    App,
    Llm,
    Tool
}

public enum HaloLogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

internal sealed record LogEntry(
    HaloLogChannel Channel,
    HaloLogLevel Level,
    string Message,
    Exception? Exception,
    DateTime TimestampLocal,
    TaskCompletionSource<bool>? FlushSignal = null
);

/// <summary>
/// 单通道日志门面（App / Llm / Tool）。
/// </summary>
public sealed class ChannelLogger
{
    private readonly HaloLogChannel _channel;

    internal ChannelLogger(HaloLogChannel channel)
    {
        _channel = channel;
    }

    public void Debug(string message) =>
        HaloLogger.Enqueue(_channel, HaloLogLevel.Debug, message, null);

    public void Info(string message) =>
        HaloLogger.Enqueue(_channel, HaloLogLevel.Info, message, null);

    public void Warn(string message, Exception? ex = null) =>
        HaloLogger.Enqueue(_channel, HaloLogLevel.Warn, message, ex);

    public void Error(string message, Exception? ex = null) =>
        HaloLogger.Enqueue(_channel, HaloLogLevel.Error, message, ex);
}

/// <summary>
/// 高吞吐无锁分级滚动日志引擎：
/// - App  -> logs/app-YYYYMMDD.log
/// - Llm  -> logs/llm-trace-YYYYMMDD.log
/// - Tool -> logs/tool-exec-YYYYMMDD.log
/// 采用 System.Threading.Channels.Channel 单消费者后台异步批量落盘，内置敏感密钥脱敏与过期日志清理。
/// </summary>
public static class HaloLogger
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object LifecycleLock = new();

    private static Channel<LogEntry>? _channel;
    private static Task? _consumerTask;
    private static CancellationTokenSource? _cts;
    private static volatile string _logsDirectory = string.Empty;
    private static volatile bool _initialized;

    // 匹配 Authorization: Bearer xxx 或常见 sk-xxx / api-key / secret 头部值
    private static readonly Regex BearerTokenRegex =
        new(
            @"(Authorization\s*:\s*Bearer\s+)([^\s""',;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

    private static readonly Regex SkTokenRegex =
        new(@"\b(sk-[A-Za-z0-9_\-]{8,})\b", RegexOptions.Compiled);

    private static readonly Regex SecretHeaderRegex =
        new(
            @"((?:x-api-key|api-key|apikey|secret|token|password)\s*[""']?\s*[:=]\s*[""']?)([^""'\s,;}{]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

    public static ChannelLogger App { get; } = new(HaloLogChannel.App);
    public static ChannelLogger Llm { get; } = new(HaloLogChannel.Llm);
    public static ChannelLogger Tool { get; } = new(HaloLogChannel.Tool);

    public static string CurrentLogsDirectory => _logsDirectory;

    public static void Initialize(StoragePathManager storageManager)
    {
        if (storageManager == null)
        {
            throw new ArgumentNullException(nameof(storageManager));
        }

        var retentionDays = storageManager.LoadAppSettings().LogRetentionDays;
        Initialize(storageManager.LogsDirectory, retentionDays);
    }

    public static void Initialize(string logsDirectory, int retentionDays = 14)
    {
        lock (LifecycleLock)
        {
            if (_initialized)
            {
                SwitchLogsDirectory(logsDirectory);
                CleanupExpiredLogs(retentionDays);
                return;
            }

            _logsDirectory = Path.GetFullPath(logsDirectory);
            Directory.CreateDirectory(_logsDirectory);

            _cts = new CancellationTokenSource();
            _channel = Channel.CreateUnbounded<LogEntry>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                }
            );

            _initialized = true;
            _consumerTask = Task
                .Factory.StartNew(
                    () => ProcessQueueAsync(_channel.Reader, _cts.Token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
                .Unwrap();

            CleanupExpiredLogs(retentionDays);
        }
    }

    /// <summary>
    /// 动态切换日志输出目录（用于用户在设置中修改存储路径后无需重启即时生效）。
    /// </summary>
    public static void SwitchLogsDirectory(string newLogsDirectory)
    {
        if (string.IsNullOrWhiteSpace(newLogsDirectory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(newLogsDirectory);
        Directory.CreateDirectory(fullPath);
        _logsDirectory = fullPath;
    }

    internal static void Enqueue(
        HaloLogChannel channel,
        HaloLogLevel level,
        string message,
        Exception? exception
    )
    {
        var activeChannel = _channel;
        if (!_initialized || activeChannel == null)
        {
            return;
        }

        var entry = new LogEntry(channel, level, message ?? string.Empty, exception, DateTime.Now);
        activeChannel.Writer.TryWrite(entry);
    }

    /// <summary>
    /// 强制刷新当前队列中的所有待写日志到磁盘。
    /// </summary>
    public static async Task FlushAsync(TimeSpan? timeout = null)
    {
        var activeChannel = _channel;
        if (!_initialized || activeChannel == null)
        {
            return;
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var sentinel = new LogEntry(
            HaloLogChannel.App,
            HaloLogLevel.Debug,
            string.Empty,
            null,
            DateTime.Now,
            tcs
        );
        if (!activeChannel.Writer.TryWrite(sentinel))
        {
            return;
        }

        var waitTimeout = timeout ?? TimeSpan.FromSeconds(5);
        await Task.WhenAny(tcs.Task, Task.Delay(waitTimeout)).ConfigureAwait(false);
    }

    public static void Flush()
    {
        FlushAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 退出时优雅排空队列并关闭后台日志线程。
    /// </summary>
    public static void Shutdown()
    {
        Task? consumer;
        CancellationTokenSource? cts;

        lock (LifecycleLock)
        {
            if (!_initialized)
            {
                return;
            }

            _initialized = false;
            _channel?.Writer.TryComplete();
            consumer = _consumerTask;
            cts = _cts;
            _consumerTask = null;
            _channel = null;
            _cts = null;
        }

        try
        {
            consumer?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 忽略退出阶段等待异常
        }
        finally
        {
            cts?.Cancel();
            cts?.Dispose();
        }
    }

    /// <summary>
    /// 对敏感信息（Bearer Token、sk- 密钥、API Key 请求头）执行掩码脱敏，保留前4位与后4位。
    /// </summary>
    public static string MaskSensitiveText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var masked = BearerTokenRegex.Replace(
            input,
            match =>
            {
                var prefix = match.Groups[1].Value;
                var token = match.Groups[2].Value;
                return prefix + MaskTokenValue(token);
            }
        );

        masked = SkTokenRegex.Replace(masked, match => MaskTokenValue(match.Groups[1].Value));

        masked = SecretHeaderRegex.Replace(
            masked,
            match =>
            {
                var prefix = match.Groups[1].Value;
                var secret = match.Groups[2].Value;
                return prefix + MaskTokenValue(secret);
            }
        );

        return masked;
    }

    public static string MaskTokenValue(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "***";
        }

        if (token.Contains("***", StringComparison.Ordinal))
        {
            return token;
        }

        if (token.Length <= 8)
        {
            return new string('*', Math.Min(token.Length, 6));
        }

        return $"{token[..4]}***{token[^4..]}";
    }

    /// <summary>
    /// 扫描 logs/ 目录并自动删除超过保留天数（默认 14 天）的历史日志文件。
    /// </summary>
    public static int CleanupExpiredLogs(int retentionDays = 14)
    {
        var dir = _logsDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return 0;
        }

        var effectiveDays = Math.Max(1, retentionDays);
        var cutoffUtc = DateTime.UtcNow.AddDays(-effectiveDays);
        var deletedCount = 0;

        try
        {
            foreach (
                var file in Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly)
            )
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    // 不删除当前活动的 cef-native.log
                    if (
                        string.Equals(
                            fileName,
                            "cef-native.log",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoffUtc)
                    {
                        info.Delete();
                        deletedCount++;
                    }
                }
                catch
                {
                    // 忽略正在被占用的单个日志文件
                }
            }
        }
        catch
        {
            // 忽略目录枚举异常
        }

        return deletedCount;
    }

    public static string GetLogFilePath(HaloLogChannel channel, DateTime? date = null)
    {
        var targetDate = (date ?? DateTime.Now).ToString("yyyyMMdd");
        var prefix = channel switch
        {
            HaloLogChannel.App => "app",
            HaloLogChannel.Llm => "llm-trace",
            HaloLogChannel.Tool => "tool-exec",
            _ => "app"
        };

        return Path.Combine(_logsDirectory, $"{prefix}-{targetDate}.log");
    }

    private static async Task ProcessQueueAsync(
        ChannelReader<LogEntry> reader,
        CancellationToken ct
    )
    {
        var batch = new List<LogEntry>(64);
        var groupedBuilders = new Dictionary<string, StringBuilder>(
            StringComparer.OrdinalIgnoreCase
        );

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                groupedBuilders.Clear();

                while (batch.Count < 256 && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                foreach (var entry in batch)
                {
                    if (entry.FlushSignal != null)
                    {
                        FlushBuildersToDisk(groupedBuilders);
                        groupedBuilders.Clear();
                        entry.FlushSignal.TrySetResult(true);
                        continue;
                    }

                    var currentDir = _logsDirectory;
                    if (string.IsNullOrWhiteSpace(currentDir))
                    {
                        continue;
                    }

                    var filePath = GetLogFilePathForDirectory(
                        currentDir,
                        entry.Channel,
                        entry.TimestampLocal
                    );
                    if (!groupedBuilders.TryGetValue(filePath, out var sb))
                    {
                        sb = new StringBuilder(512);
                        groupedBuilders[filePath] = sb;
                    }

                    FormatLogEntry(sb, entry);
                }

                FlushBuildersToDisk(groupedBuilders);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消退出
        }
        catch
        {
            // 防止日志线程异常导致进程崩溃
        }
    }

    private static string GetLogFilePathForDirectory(
        string logsDir,
        HaloLogChannel channel,
        DateTime timestampLocal
    )
    {
        var datePart = timestampLocal.ToString("yyyyMMdd");
        var prefix = channel switch
        {
            HaloLogChannel.App => "app",
            HaloLogChannel.Llm => "llm-trace",
            HaloLogChannel.Tool => "tool-exec",
            _ => "app"
        };

        return Path.Combine(logsDir, $"{prefix}-{datePart}.log");
    }

    private static void FormatLogEntry(StringBuilder sb, LogEntry entry)
    {
        var sanitizedMessage = MaskSensitiveText(entry.Message);
        var levelTag = entry.Level switch
        {
            HaloLogLevel.Debug => "DEBUG",
            HaloLogLevel.Info => "INFO ",
            HaloLogLevel.Warn => "WARN ",
            HaloLogLevel.Error => "ERROR",
            _ => "INFO "
        };

        sb.Append('[')
            .Append(entry.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append("] [")
            .Append(levelTag)
            .Append("] [")
            .Append(entry.Channel.ToString().ToUpperInvariant())
            .Append("] ")
            .AppendLine(sanitizedMessage);

        if (entry.Exception != null)
        {
            sb.Append("  Exception: ").AppendLine(MaskSensitiveText(entry.Exception.ToString()));
        }
    }

    private static void FlushBuildersToDisk(Dictionary<string, StringBuilder> groupedBuilders)
    {
        foreach (var (filePath, sb) in groupedBuilders)
        {
            if (sb.Length == 0)
            {
                continue;
            }

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096
                );
                using var sw = new StreamWriter(fs, Utf8NoBom);
                sw.Write(sb.ToString());
                sw.Flush();
            }
            catch
            {
                // 忽略磁盘写入瞬时冲突
            }
        }
    }
}
