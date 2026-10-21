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
