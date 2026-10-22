using System.Collections.Concurrent;
using Host.Desktop.Infrastructure.Network;

namespace Host.Desktop.LlmGateway;

/// <summary>P0-2 传输健康：同 origin 连续 2 次无响应才判死；30s 节流重建，先装新后关旧。</summary>
public static class ProviderTransportHealth
{
    private sealed record OriginState(int ConsecutiveFailures, DateTime LastRebuildUtc);

    private static readonly ConcurrentDictionary<string, OriginState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public static string OriginOf(string url)
    {
        try
        {
            return new Uri(url).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        catch
        {
            return url ?? string.Empty;
        }
    }

    public static void ObserveSuccess(string origin) =>
        _states.AddOrUpdate(
            origin,
            new OriginState(0, DateTime.MinValue),
            (_, s) => s with { ConsecutiveFailures = 0 }
        );

    /// <returns>是否需要重建</returns>
    public static bool ObserveFailure(string origin, string route = "direct")
    {
        var s = _states.AddOrUpdate(
            origin,
            new OriginState(1, DateTime.MinValue),
            (_, old) => old with { ConsecutiveFailures = old.ConsecutiveFailures + 1 }
        );
        if (s.ConsecutiveFailures < 2)
            return false;
        if (DateTime.UtcNow - s.LastRebuildUtc < TimeSpan.FromSeconds(30))
            return false;
        return true;
    }

    public static void MarkRebuilt(string origin) =>
        _states.AddOrUpdate(
            origin,
            new OriginState(0, DateTime.UtcNow),
            (_, s) => s with { ConsecutiveFailures = 0, LastRebuildUtc = DateTime.UtcNow }
        );

    public static void ResetForTests() => _states.Clear();
}
