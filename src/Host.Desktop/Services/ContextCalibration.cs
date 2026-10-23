using System;
using System.Collections.Generic;
using System.Linq;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-2：上下文校准（长期漂移收敛）。
/// 双序列：trailingRatio（有锚）与 unanchored（无锚）；窗口 16 中位数。
/// Correct()：有锚只 scale 尾、无锚同 scale；带内用 ratio，否则 +overhead（中位数，上限 32k）。
/// 不对称收敛：上行即时、下行需 3 连击且每步 ≤15% + 冻结 2 连 misreport + 硬地板 0.85×raw / 顶 6× + boundary guard；
/// [0.5,3] 外丢弃。校准只影响估算，不改账单真值。
/// </summary>
public sealed class ContextCalibration
{
    public const int WindowSize = 16;
    public const double MinRatio = 0.5;
    public const double MaxRatio = 3.0;
    public const double DownStepMax = 0.15;
    public const int DownStreakRequired = 3;
    public const int MisreportFreeze = 2;
    public const double FloorFactor = 0.85;
    public const double CeilFactor = 6.0;
    public const int MaxOverhead = 32768;

    private readonly List<double> _trailingSamples = new();
    private readonly List<double> _unanchoredSamples = new();
    private readonly List<double> _overheadSamples = new();
    private readonly object _lock = new();

    private double _trailingRatio = 1.0;
    private double _unanchoredRatio = 1.0;
    private int _trailingDownStreak;
    private int _unanchoredDownStreak;
    private int _freeze;

    /// <summary>有锚观测：tailEstimate（锚后估算）vs tailActual（实际尾量）。</summary>
    public void RecordAnchored(double tailEstimate, double tailActual)
    {
        if (tailEstimate <= 0)
            return;
        Observe(tailActual / tailEstimate, anchored: true, tailActual - tailEstimate);
    }

    /// <summary>无锚观测：estimate（全量估算）vs actual（网关真值）。</summary>
    public void RecordUnanchored(double estimate, double actual)
    {
        if (estimate <= 0)
            return;
        Observe(actual / estimate, anchored: false, actual - estimate);
    }

    /// <summary>有锚修正：只 scale 尾。raw = anchor + tail。</summary>
    public int CorrectAnchored(long anchorUsage, int tailEstimate)
    {
        double ratio;
        lock (_lock)
            ratio = _trailingRatio;
        double raw = Math.Max(0, anchorUsage) + Math.Max(0, tailEstimate);
        double corrected = Math.Max(0, anchorUsage) + Math.Max(0, tailEstimate) * ratio;
        return ClampToBand(corrected, raw);
    }

    /// <summary>无锚修正：有 ratio 用 ratio scale，否则 +overhead（中位数，上限 32k）。</summary>
    public int CorrectUnanchored(int rawEstimate)
    {
        double ratio;
        double overhead;
        bool hasRatio;
        lock (_lock)
        {
            ratio = _unanchoredRatio;
            hasRatio = _unanchoredSamples.Count > 0;
            overhead = Median(_overheadSamples);
        }
        double raw = Math.Max(0, rawEstimate);
        double corrected = hasRatio ? raw * ratio : raw + Math.Clamp(overhead, 0, MaxOverhead);
        return ClampToBand(corrected, raw);
    }

    public double TrailingRatio
    {
        get
        {
            lock (_lock)
                return _trailingRatio;
        }
    }

    public double UnanchoredRatio
    {
        get
        {
            lock (_lock)
                return _unanchoredRatio;
        }
    }

    public double UnanchoredOverheadMedian
    {
        get
        {
            lock (_lock)
                return Math.Clamp(Median(_overheadSamples), 0, MaxOverhead);
        }
    }

    private void Observe(double ratio, bool anchored, double overheadDelta)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            return;
        lock (_lock)
        {
            // [0.5,3] 外丢弃 + 冻结 2 连 misreport。
            if (ratio < MinRatio || ratio > MaxRatio)
            {
                _freeze = MisreportFreeze;
                return;
            }
            if (_freeze > 0)
            {
                _freeze--;
                return;
            }
            var samples = anchored ? _trailingSamples : _unanchoredSamples;
            samples.Add(ratio);
            while (samples.Count > WindowSize)
                samples.RemoveAt(0);
            if (!anchored)
            {
                _overheadSamples.Add(overheadDelta);
                while (_overheadSamples.Count > WindowSize)
                    _overheadSamples.RemoveAt(0);
            }
            // 不对称收敛：上行即时，下行需 3 连击且每步 ≤15%。
            ref double current = ref (anchored ? ref _trailingRatio : ref _unanchoredRatio);
            ref int streak = ref (anchored ? ref _trailingDownStreak : ref _unanchoredDownStreak);
            if (ratio >= current)
            {
                current = ratio;
                streak = 0;
            }
            else
            {
                streak++;
                if (streak >= DownStreakRequired)
                {
                    current = Math.Max(ratio, current * (1.0 - DownStepMax));
                    streak = 0;
                }
            }
        }
    }

    /// <summary>硬地板 0.85×raw / 顶 6× + boundary guard（非负、int 上限）。</summary>
    private static int ClampToBand(double corrected, double raw)
    {
        double lo = raw * FloorFactor;
        double hi = raw <= 0 ? MaxOverhead : raw * CeilFactor;
        double v = Math.Clamp(corrected, lo, hi);
        if (v < 0)
            v = 0;
        if (v > int.MaxValue)
            v = int.MaxValue;
        return (int)Math.Round(v);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        if (n % 2 == 1)
            return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    public void ClearForTests()
    {
        lock (_lock)
        {
            _trailingSamples.Clear();
            _unanchoredSamples.Clear();
            _overheadSamples.Clear();
            _trailingRatio = 1.0;
            _unanchoredRatio = 1.0;
            _trailingDownStreak = 0;
            _unanchoredDownStreak = 0;
            _freeze = 0;
        }
    }
}
