using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 发货加固可观测：轮末刷盘分位、检查点失败、WAL 体积、完整性失败计数。
/// 全部走进程内计数 + 阈值日志，不抛错，不阻断读写主路径。
/// </summary>
public static class TimelineObservability
{
    public const long SlowFlushThresholdMs = 1000;
    public const long WalWarnThresholdBytes = 8L * 1024 * 1024;
    private const int MaxFlushSamples = 256;

    private static readonly object Sync = new();
    private static readonly List<long> FlushSamples = new();
    private static long _slowFlushCount;
    private static long _checkpointFailCount;
    private static long _integrityFailCount;
    private static long _integrityOkCount;
    private static long _walWarnCount;
    private static long _lastWalBytes;

    public static void ReportFlush(long flushMs)
    {
        if (flushMs < 0)
        {
            return;
        }
        lock (Sync)
        {
            FlushSamples.Add(flushMs);
            if (FlushSamples.Count > MaxFlushSamples)
            {
                FlushSamples.RemoveRange(0, FlushSamples.Count - MaxFlushSamples);
            }
            if (flushMs >= SlowFlushThresholdMs)
            {
                _slowFlushCount++;
            }
        }
        if (flushMs >= SlowFlushThresholdMs)
        {
            try
            {
                var snap = Snapshot();
                HaloLogger.App.Warn(
                    "[Timeline] slow flushMs="
                        + flushMs
                        + " median="
                        + snap.MedianMs
                        + " tail95="
                        + snap.TailMs
                        + " slowCount="
                        + snap.SlowFlushCount
                );
            }
            catch { }
        }
    }

    public static void ReportCheckpoint(bool ok)
    {
        if (ok)
        {
            return;
        }
        lock (Sync)
        {
            _checkpointFailCount++;
        }
        try
        {
            HaloLogger.App.Warn("[Timeline] wal checkpoint failed.");
        }
        catch { }
    }

    public static void ReportIntegrity(bool ok)
    {
        lock (Sync)
        {
            if (ok)
            {
                _integrityOkCount++;
            }
            else
            {
                _integrityFailCount++;
            }
        }
        if (!ok)
        {
            try
            {
                HaloLogger.App.Warn("[Timeline] integrity check failed.");
            }
            catch { }
        }
    }

    public static void ReportWalSize(long bytes)
    {
        if (bytes < 0)
        {
            return;
        }
        lock (Sync)
        {
            _lastWalBytes = bytes;
            if (bytes >= WalWarnThresholdBytes)
            {
                _walWarnCount++;
            }
        }
        if (bytes >= WalWarnThresholdBytes)
        {
            try
            {
                HaloLogger.App.Warn("[Timeline] wal volume bytes=" + bytes);
            }
            catch { }
        }
    }

    public readonly record struct TimelineObserveSnapshot(
        long MedianMs,
        long TailMs,
        long MaxMs,
        int Samples,
        long SlowFlushCount,
        long CheckpointFailCount,
        long IntegrityFailCount,
        long IntegrityOkCount,
        long WalWarnCount,
        long LastWalBytes
    );

    public static TimelineObserveSnapshot Snapshot()
    {
        lock (Sync)
        {
            long median = 0;
            long tail = 0;
            long max = 0;
            if (FlushSamples.Count > 0)
            {
                var ordered = FlushSamples.OrderBy(v => v).ToList();
                median = ordered[ordered.Count / 2];
                int tailIndex = (int)((ordered.Count - 1) * 0.95);
                tail = ordered[Math.Clamp(tailIndex, 0, ordered.Count - 1)];
                max = ordered[ordered.Count - 1];
            }
            return new TimelineObserveSnapshot(
                median,
                tail,
                max,
                FlushSamples.Count,
                _slowFlushCount,
                _checkpointFailCount,
                _integrityFailCount,
                _integrityOkCount,
                _walWarnCount,
                _lastWalBytes
            );
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            FlushSamples.Clear();
            _slowFlushCount = 0;
            _checkpointFailCount = 0;
            _integrityFailCount = 0;
            _integrityOkCount = 0;
            _walWarnCount = 0;
            _lastWalBytes = 0;
        }
    }
}
